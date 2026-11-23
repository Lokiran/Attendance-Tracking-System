using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Attendance;

/// <summary>Merged (start, end) periods a person's committed timesheets cover.</summary>
public sealed record Periods(IReadOnlyList<(DateOnly Start, DateOnly End)> Items)
{
    public static Periods Empty { get; } = new([]);
}

/// <summary>Day records of a set of people, and the cache key they were stored under (null when not cached).</summary>
public sealed record ComputedRecords(IReadOnlyDictionary<int, IReadOnlyList<DayRecord>> ByEmployee, string? CacheKey);

/// <summary>
/// Loads attendance inputs in bulk and feeds them to the rules engine (<c>app/services/attendance_data.py</c>).
/// A committed upload covers its period only for the people listed in it, so a partial file never makes anyone else
/// absent. Large scopes are read with one range scan instead of thousands of IN parameters, and the records of a scope
/// are shared between the requests of one page through <see cref="RecordCache"/>.
/// </summary>
public sealed class AttendanceData(IAtsDb db, IClock clock, AtsSettings settings, RecordCache cache)
{
    private const int Chunk = 500;

    /// <summary>Above this many people one range query (filtered in memory) beats many IN (...) chunks.</summary>
    private const int RangeScanMin = 2000;

    /// <summary>Scopes smaller than this are cheap to compute and are not cached.</summary>
    public const int CacheMinEmployees = 25;

    public DateOnly Today => OfficeTime.LocalDate(clock.UtcNow, settings.TimeZone);

    public static ScheduleData ScheduleDataOf(WorkSchedule s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new ScheduleData(s.Id, s.EmployeeId, s.DepartmentId, s.StartTime, s.EndTime, s.RequiredMinutes,
                                s.WorkingDays.ToHashSet(), s.EffectiveFrom, s.EffectiveTo, s.Name);
    }

    public static EmployeeData EmployeeDataOf(User u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return new EmployeeData(u.Id, u.DepartmentId, u.DateJoined);
    }

    /// <summary>The work mode and office hours one attendance_daily row stands for (null: it says nothing).</summary>
    public static TimesheetDay? TimesheetDayOf(int eid, DateOnly d, WorkMode? overrideMode, WorkMode? uploadMode, int? uploadMinutes,
                                               int? badgeMinutes, int? wifiMinutes, DateTimeOffset? firstIn, DateTimeOffset? lastOut)
    {
        if (lastOut is not null && (firstIn is null || lastOut < firstIn))
        {
            lastOut = null;
        }

        if (overrideMode is null && badgeMinutes is null && (wifiMinutes ?? 0) == 0)
        {
            // Fast path for the usual row, identical to classify_day: an OFFICE row's hours are its hours column;
            // other modes carry no office hours; hours without a mode mean WFH (INFERRED).
            if (uploadMode is { } mode)
            {
                return new TimesheetDay(eid, d, mode, mode == WorkMode.Office ? uploadMinutes : null, firstIn, lastOut);
            }

            return (uploadMinutes ?? 0) != 0 ? new TimesheetDay(eid, d, WorkMode.Wfh, null, firstIn, lastOut) : null;
        }

        var cls = WfoRules.ClassifyDay(new DayEvidence(Override: overrideMode, UploadMode: uploadMode, UploadMinutes: uploadMinutes,
                                                       BadgeMinutes: badgeMinutes, WifiMinutes: wifiMinutes));
        if (cls.WorkMode is null)
        {
            return null;
        }

        var minutes = cls.WorkMode == WorkMode.Office && cls.HoursKnown ? cls.OfficeMinutes : (int?)null;
        return new TimesheetDay(eid, d, cls.WorkMode, minutes, firstIn, lastOut);
    }

    public async Task<Dictionary<int, List<TimesheetDay>>> LoadTimesheetAsync(IReadOnlyList<int> employeeIds, DateOnly start, DateOnly end,
                                                                              CancellationToken ct = default)
    {
        var output = new Dictionary<int, List<TimesheetDay>>();
        var baseQuery = db.AttendanceDaily.AsNoTracking().Where(a => a.WorkDate >= start && a.WorkDate <= end);
        var queries = new List<IQueryable<AttendanceDaily>>();
        HashSet<int>? wanted = null;
        if (employeeIds.Count >= RangeScanMin)
        {
            wanted = [.. employeeIds];
            queries.Add(baseQuery);
        }
        else
        {
            for (var i = 0; i < employeeIds.Count; i += Chunk)
            {
                var chunk = employeeIds.Skip(i).Take(Chunk).ToList();
                queries.Add(baseQuery.Where(a => chunk.Contains(a.EmployeeId)));
            }
        }

        foreach (var q in queries)
        {
            var rows = await q.Select(a => new
            {
                a.EmployeeId, a.WorkDate, a.OverrideWorkMode, a.UploadWorkMode, a.UploadMinutes, a.BadgeMinutes, a.WifiMinutes,
                a.FirstIn, a.LastOut,
            }).ToListAsync(ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                if (wanted is not null && !wanted.Contains(r.EmployeeId))
                {
                    continue;
                }

                if (TimesheetDayOf(r.EmployeeId, r.WorkDate, r.OverrideWorkMode, r.UploadWorkMode, r.UploadMinutes, r.BadgeMinutes,
                                   r.WifiMinutes, r.FirstIn, r.LastOut) is { } t)
                {
                    if (!output.TryGetValue(r.EmployeeId, out var list))
                    {
                        output[r.EmployeeId] = list = [];
                    }

                    list.Add(t);
                }
            }
        }

        return output;
    }

    private IQueryable<AttendanceUpload> Committed =>
        db.AttendanceUploads.Where(u => u.Status == UploadStatus.Committed && u.PeriodStart != null && u.PeriodEnd != null);

    /// <summary>Per employee, the merged periods of the committed timesheets that list them (valid rows only).</summary>
    public async Task<Dictionary<int, Periods>> CoverageAsync(IReadOnlyList<int> employeeIds, CancellationToken ct = default)
    {
        var baseQuery = from r in db.AttendanceUploadRows.AsNoTracking()
                        join u in Committed on r.UploadId equals u.Id
                        where r.Error == null && r.EmployeeId != null
                        select new { EmployeeId = r.EmployeeId!.Value, Start = u.PeriodStart!.Value, End = u.PeriodEnd!.Value };
        var found = new Dictionary<int, HashSet<(DateOnly, DateOnly)>>();
        HashSet<int>? wanted = null;
        var rows = new List<(int EmployeeId, DateOnly Start, DateOnly End)>();
        if (employeeIds.Count >= RangeScanMin)
        {
            wanted = [.. employeeIds];
            rows.AddRange((await baseQuery.Distinct().ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.EmployeeId, x.Start, x.End)));
        }
        else
        {
            for (var i = 0; i < employeeIds.Count; i += Chunk)
            {
                var chunk = employeeIds.Skip(i).Take(Chunk).ToList();
                rows.AddRange((await baseQuery.Where(x => chunk.Contains(x.EmployeeId)).Distinct().ToListAsync(ct).ConfigureAwait(false))
                              .Select(x => (x.EmployeeId, x.Start, x.End)));
            }
        }

        foreach (var (eid, a, b) in rows)
        {
            if (wanted is not null && !wanted.Contains(eid))
            {
                continue;
            }

            if (!found.TryGetValue(eid, out var set))
            {
                found[eid] = set = [];
            }

            set.Add((a, b));
        }

        return found.ToDictionary(kv => kv.Key, kv => Merge(kv.Value));
    }

    /// <summary>Sorted periods, adjacent ones (gap of at most one day) merged.</summary>
    public static Periods Merge(IEnumerable<(DateOnly Start, DateOnly End)> periods)
    {
        var merged = new List<(DateOnly Start, DateOnly End)>();
        foreach (var (a, b) in periods.Order())
        {
            if (merged.Count > 0 && a.DayNumber - merged[^1].End.DayNumber <= 1)
            {
                merged[^1] = (merged[^1].Start, b > merged[^1].End ? b : merged[^1].End);
            }
            else
            {
                merged.Add((a, b));
            }
        }

        return new Periods(merged);
    }

    /// <summary>Whether a date falls in one of the periods (bisect over the starts).</summary>
    public static Func<DateOnly, bool> CoveredFn(Periods periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        var items = periods.Items;
        var starts = items.Select(p => p.Start).ToArray();
        return d =>
        {
            var i = Array.BinarySearch(starts, d);
            i = i >= 0 ? LastIndexOf(starts, i) : ~i - 1; // bisect_right - 1
            return i >= 0 && d <= items[i].End;
        };
    }

    private static int LastIndexOf(DateOnly[] starts, int i)
    {
        while (i + 1 < starts.Length && starts[i + 1] == starts[i])
        {
            i++;
        }

        return i;
    }

    public async Task<(List<ScheduleData> Schedules, List<HolidayData> Holidays, PolicyData Policy)> LoadRulesInputsAsync(
        DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        var schedules = (await db.WorkSchedules.AsNoTracking()
                .Where(s => s.EffectiveFrom <= end && (s.EffectiveTo == null || s.EffectiveTo >= start))
                .OrderBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(ScheduleDataOf).ToList();
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.HolidayDate >= start && h.HolidayDate <= end).OrderBy(h => h.Id)
            .Select(h => new HolidayData(h.HolidayDate, h.Name, h.DepartmentId)).ToListAsync(ct).ConfigureAwait(false);
        var policy = Policies.Data(await Policies.GetAsync(db, ct).ConfigureAwait(false));
        return (schedules, holidays, policy);
    }

    private async Task<string> CoverageFingerprintAsync(CancellationToken ct)
    {
        var count = await Committed.CountAsync(ct).ConfigureAwait(false);
        var maxId = await Committed.MaxAsync(u => (int?)u.Id, ct).ConfigureAwait(false);
        var maxCommitted = await Committed.MaxAsync(u => u.CommittedAt, ct).ConfigureAwait(false);
        return $"{count}|{maxId}|{maxCommitted?.UtcTicks}";
    }

    /// <summary>Changes whenever a timesheet row in the range is added, changed or removed.</summary>
    private async Task<string> TimesheetFingerprintAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = db.AttendanceDaily.Where(a => a.WorkDate >= start && a.WorkDate <= end);
        var count = await rows.CountAsync(ct).ConfigureAwait(false);
        var maxUpdated = await rows.MaxAsync(a => (DateTimeOffset?)a.UpdatedAt, ct).ConfigureAwait(false);
        var maxId = await rows.MaxAsync(a => (int?)a.Id, ct).ConfigureAwait(false);
        return $"{count}|{maxUpdated?.UtcTicks}|{maxId}";
    }

    /// <summary>Day records per employee for the range (clipped to today). Callers must not modify them.</summary>
    public async Task<ComputedRecords> ComputeRecordsAsync(IReadOnlyList<User> employees, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employees);
        var zone = settings.TimeZone;
        var today = Today;
        var (schedules, holidays, policy) = await LoadRulesInputsAsync(start, end, ct).ConfigureAwait(false);
        var people = employees.Select(EmployeeDataOf).ToList();

        async Task<IReadOnlyDictionary<int, IReadOnlyList<DayRecord>>> Compute()
        {
            var ids = people.Select(p => p.Id).ToList();
            var timesheet = await LoadTimesheetAsync(ids, start, end, ct).ConfigureAwait(false);
            var periods = await CoverageAsync(ids, ct).ConfigureAwait(false);
            var shared = new RulesMemo(); // schedule / shift-start memo reused across everyone in this computation
            var result = new Dictionary<int, IReadOnlyList<DayRecord>>(people.Count);
            foreach (var p in people)
            {
                result[p.Id] = AttendanceRules.BuildDayRecords(p, start, end, today, zone,
                    timesheet.TryGetValue(p.Id, out var t) ? t : [], schedules, holidays, policy,
                    CoveredFn(periods.GetValueOrDefault(p.Id) ?? Periods.Empty), shared);
            }

            return result;
        }

        if (people.Count < CacheMinEmployees)
        {
            return new ComputedRecords(await Compute().ConfigureAwait(false), null);
        }

        var key = CacheKey(people, start, end, today, schedules, holidays, policy,
                           await CoverageFingerprintAsync(ct).ConfigureAwait(false),
                           await TimesheetFingerprintAsync(start, end, ct).ConfigureAwait(false), cache.WriteVersion);
        var size = people.Count * (Math.Min(end.DayNumber, today.DayNumber) - start.DayNumber + 1);
        var records = await cache.GetOrComputeAsync(key, Math.Max(size, 0), Compute).ConfigureAwait(false);
        return new ComputedRecords(records, key);
    }

    /// <summary>Everything the result depends on, as a digest (Python uses the full tuple as the key).</summary>
    private static string CacheKey(List<EmployeeData> people, DateOnly start, DateOnly end, DateOnly today, List<ScheduleData> schedules,
                                   List<HolidayData> holidays, PolicyData policy, string coverage, string timesheet, long version)
    {
        var sb = new StringBuilder();
        foreach (var p in people)
        {
            sb.Append(p.Id).Append(',').Append(p.DepartmentId).Append(',').Append(p.DateJoined.DayNumber).Append(';');
        }

        sb.Append('|').Append(start.DayNumber).Append('|').Append(end.DayNumber).Append('|').Append(today.DayNumber).Append('|');
        foreach (var s in schedules)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{s.Id},{s.EmployeeId},{s.DepartmentId},{s.StartTime.Ticks},{s.EndTime.Ticks},{s.RequiredMinutes},")
              .Append(string.Join('.', s.WorkingDays.Order())).Append(CultureInfo.InvariantCulture, $",{s.EffectiveFrom.DayNumber},{s.EffectiveTo?.DayNumber},{s.Name};");
        }

        foreach (var h in holidays)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{h.HolidayDate.DayNumber},{h.DepartmentId},{h.Name};");
        }

        sb.Append(CultureInfo.InvariantCulture, $"|{policy.MinPresentMinutes},{policy.LateGraceMinutes}|{coverage}|{timesheet}|{version}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
