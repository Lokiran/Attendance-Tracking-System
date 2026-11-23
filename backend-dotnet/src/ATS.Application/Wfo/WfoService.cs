using System.Globalization;
using System.Text.RegularExpressions;
using Ats.Application.Attendance;
using Ats.Application.Common;
using Ats.Application.Data;
using Ats.Application.Scope;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Wfo;

/// <summary>A classified day with what it was classified from.</summary>
public sealed record ClassifiedDay(DateOnly Date, DayClass Class, bool Scheduled, DayEvidence Evidence, AttendanceDaily? Row,
                                   DateTimeOffset? FirstIn, DateTimeOffset? LastOut);

/// <summary>Schedule and holiday facts for one employee (also sizes the nominal week).</summary>
public sealed class WfoCalendar(IReadOnlyList<ScheduleData> schedules, Dictionary<DateOnly, string> holidays, EmployeeData employee)
{
    public Dictionary<DateOnly, string> Holidays { get; } = holidays;

    public EmployeeData Employee { get; } = employee;

    public bool Scheduled(DateOnly d)
    {
        if (d < Employee.DateJoined)
        {
            return false;
        }

        var s = AttendanceRules.ResolveSchedule(schedules, Employee, d);
        return s is not null && s.WorkingDays.Contains(OfficeTime.IsoWeekday(d));
    }
}

public sealed record EmployeeDays(User Employee, IReadOnlyList<ClassifiedDay> Days, WfoCalendar Calendar,
                                  Dictionary<DateOnly, AttendanceDaily> RowsByDate);

/// <summary>
/// WFO reads (<c>app/services/wfo.py</c>): each employee-day's evidence is classified with the pure rules and summarised
/// against the policy. Reads always recompute from the evidence, so they are never stale.
/// </summary>
public sealed partial class WfoService(IAtsDb db, AttendanceData data, ScopeService scope)
{
    /// <summary>Six days either side, so the weeks around a period can be sized.</summary>
    public const int WeekPad = 6;

    /// <summary>"YYYY-MM" (default: the current month) -> (label, first day, last day).</summary>
    public static (string Label, DateOnly Start, DateOnly End) MonthBounds(string? month, DateOnly today)
    {
        int y, m;
        if (string.IsNullOrEmpty(month))
        {
            (y, m) = (today.Year, today.Month);
        }
        else
        {
            var match = MonthPattern().Match(month.Trim());
            if (!match.Success || !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out m) || m is < 1 or > 12
                || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out y) || y is < 2000 or > 2100)
            {
                throw new ValidationFailedException("month must look like YYYY-MM");
            }
        }

        var start = new DateOnly(y, m, 1);
        return ($"{y:0000}-{m:00}", start, OfficeTime.MonthEnd(start));
    }

    public static EmployeeBrief Brief(User u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return new(u.Id, u.EmployeeCode, u.FullName, u.Department?.Name, u.ManagerId, u.Manager?.FullName);
    }

    /// <summary>
    /// Classified days start..end per employee (future days included). <paramref name="overrides"/> replaces evidence for
    /// (employee, date) pairs (the upload preview); <paramref name="joinedFrom"/> is the earlier joining date a commit would
    /// set for people added from a timesheet.
    /// </summary>
    public async Task<Dictionary<int, EmployeeDays>> ClassifyRangeAsync(IReadOnlyList<User> employees, DateOnly start, DateOnly end,
        IReadOnlyDictionary<(int, DateOnly), Func<DayEvidence, DayEvidence>>? overrides = null,
        IReadOnlyDictionary<int, DateOnly>? joinedFrom = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employees);
        var ids = employees.Select(e => e.Id).ToList();
        var (schedules, holidays, _) = await data.LoadRulesInputsAsync(start.AddDays(-WeekPad), end.AddDays(WeekPad), ct).ConfigureAwait(false);
        var rows = new Dictionary<(int, DateOnly), AttendanceDaily>();
        for (var i = 0; i < ids.Count; i += 500)
        {
            var chunk = ids.Skip(i).Take(500).ToList();
            foreach (var r in await db.AttendanceDaily.Where(a => chunk.Contains(a.EmployeeId) && a.WorkDate >= start && a.WorkDate <= end)
                         .ToListAsync(ct).ConfigureAwait(false))
            {
                rows[(r.EmployeeId, r.WorkDate)] = r;
            }
        }

        var output = new Dictionary<int, EmployeeDays>();
        foreach (var e in employees)
        {
            var emp = AttendanceData.EmployeeDataOf(e);
            if (joinedFrom is not null && joinedFrom.TryGetValue(e.Id, out var from) && from < emp.DateJoined)
            {
                emp = emp with { DateJoined = from };
            }

            var holidayMap = new Dictionary<DateOnly, string>();
            foreach (var h in holidays.Where(h => h.DepartmentId is null || h.DepartmentId == e.DepartmentId))
            {
                holidayMap[h.HolidayDate] = h.Name; // dict comprehension: the last one wins (P2)
            }

            var cal = new WfoCalendar(schedules, holidayMap, emp);
            var days = new List<ClassifiedDay>(end.DayNumber - start.DayNumber + 1);
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                var row = rows.GetValueOrDefault((e.Id, d));
                var workday = cal.Scheduled(d);
                var holiday = workday && holidayMap.ContainsKey(d);
                var evidence = new DayEvidence(ScheduledWorkday: workday, Holiday: holiday, Override: row?.OverrideWorkMode,
                                               UploadMode: row?.UploadWorkMode, UploadMinutes: row?.UploadMinutes,
                                               BadgeMinutes: row?.BadgeMinutes, WifiMinutes: row?.WifiMinutes);
                if (overrides is not null && overrides.TryGetValue((e.Id, d), out var change))
                {
                    evidence = change(evidence);
                }

                var cls = d >= emp.DateJoined ? WfoRules.ClassifyDay(evidence) : DayClass.None;
                days.Add(new ClassifiedDay(d, cls, workday && !holiday, evidence, row, row?.FirstIn, row?.LastOut));
            }

            output[e.Id] = new EmployeeDays(e, days, cal, rows.Where(kv => kv.Key.Item1 == e.Id).ToDictionary(kv => kv.Key.Item2, kv => kv.Value));
        }

        return output;
    }

    public static PolicyDay PolicyDayOf(ClassifiedDay c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new PolicyDay(c.Date, c.Class.WorkMode, c.Class.OfficeMinutes, c.Scheduled, c.Class.HoursKnown);
    }

    public static WfoSummary Summarize(EmployeeDays ed, DateOnly start, DateOnly end, DateOnly today, WfoPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(ed);
        return WfoRules.SummarizeWfo(ed.Days.Select(PolicyDayOf), start, end, today, policy, ed.Calendar.Scheduled);
    }

    public async Task<EmployeeWfoOut> EmployeeMonthAsync(User employee, string? month, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);
        var today = data.Today;
        var (label, start, end) = MonthBounds(month, today);
        var policy = Policies.Wfo(await Policies.GetAsync(db, ct).ConfigureAwait(false));
        var ed = (await ClassifyRangeAsync([employee], start.AddDays(-WeekPad), end.AddDays(WeekPad), ct: ct).ConfigureAwait(false))[employee.Id];
        var s = Summarize(ed, start, end, today, policy);
        var days = ed.Days.Where(c => start <= c.Date && c.Date <= end).Select(c => new WfoDay(
            c.Date, c.Class.WorkMode, c.Class.Source, WfoRules.IsWfoDay(PolicyDayOf(c), policy), c.Class.OfficeMinutes, c.Class.WfhMinutes,
            [.. c.Class.Conflicts], c.Scheduled, c.Evidence.Override is not null, c.Row?.OverrideNote, c.Date == today, c.Date > today)).ToList();
        return new EmployeeWfoOut(Brief(employee), label, start, end, today, WfoPolicyOut.Of(policy), WfoSummaryOut.Of(s), WfoWeek.Of(s), days);
    }

    /// <summary>WFO figures for an arbitrary range (added to the attendance summary).</summary>
    public async Task<WfoRangeSummary> RangeSummaryAsync(User employee, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);
        var today = data.Today;
        var policy = Policies.Wfo(await Policies.GetAsync(db, ct).ConfigureAwait(false));
        var ed = (await ClassifyRangeAsync([employee], start.AddDays(-WeekPad), end.AddDays(WeekPad), ct: ct).ConfigureAwait(false))[employee.Id];
        return WfoRangeSummary.Of(Summarize(ed, start, end, today, policy));
    }

    public async Task<List<WfoRow>> RowsForAsync(IReadOnlyList<User> employees, DateOnly start, DateOnly end, DateOnly today, WfoPolicy policy,
                                                 CancellationToken ct = default)
    {
        var rows = new List<WfoRow>(employees.Count);
        for (var i = 0; i < employees.Count; i += 200)
        {
            var chunk = employees.Skip(i).Take(200).ToList();
            var classified = await ClassifyRangeAsync(chunk, start.AddDays(-WeekPad), end.AddDays(WeekPad), ct: ct).ConfigureAwait(false);
            rows.AddRange(chunk.Select(e => WfoRow.Of(WfoSummaryOut.Of(Summarize(classified[e.Id], start, end, today, policy)), Brief(e))));
        }

        return rows;
    }

    public static WfoTotals Totals(IReadOnlyCollection<WfoRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var working = rows.Sum(r => r.WorkingDays);
        var wfo = rows.Sum(r => r.WfoDays);
        return new WfoTotals(rows.Count, working, wfo, rows.Sum(r => r.WfhDays), working != 0 ? PyMath.Round(wfo * 100.0 / working, 1) : null,
                             rows.Count(r => r.Compliance == "COMPLIANT"), rows.Count(r => r.Compliance == "NON_COMPLIANT"),
                             rows.Count(r => r.WfoBand == "MET"), rows.Count(r => r.WfoBand == "NEAR"), rows.Count(r => r.WfoBand == "BELOW"));
    }

    /// <summary>Manager: their whole reporting line plus themselves; admin: everyone tracked.</summary>
    public async Task<List<User>> TeamScopeAsync(User viewer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var people = await scope.ResolveScopeAsync(viewer, ct: ct).ConfigureAwait(false);
        if (viewer.AttendanceTracked && viewer.IsActive && people.All(p => p.Id != viewer.Id))
        {
            people.Insert(0, viewer);
        }

        return people;
    }

    public async Task<TeamWfoOut> TeamMonthAsync(User viewer, string? month, int? employeeId, CancellationToken ct = default)
    {
        var today = data.Today;
        var (label, start, end) = MonthBounds(month, today);
        var policy = Policies.Wfo(await Policies.GetAsync(db, ct).ConfigureAwait(false));
        var people = await TeamScopeAsync(viewer, ct).ConfigureAwait(false);
        if (employeeId is { } id)
        {
            people = [.. people.Where(p => p.Id == id)];
            if (people.Count == 0)
            {
                throw new ForbiddenException("That employee is not in your team");
            }
        }

        var rows = await RowsForAsync(people, start, end, today, policy, ct).ConfigureAwait(false);
        return new TeamWfoOut(label, start, end, WfoPolicyOut.Of(policy), Totals(rows), rows);
    }

    /// <summary>An employee the viewer may see WFO for; 403 (not 404) for anyone else, as the WFO API specifies (P6).</summary>
    public async Task<User> ScopedEmployeeAsync(User viewer, int employeeId, CancellationToken ct = default) =>
        (await TeamScopeAsync(viewer, ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == employeeId)
        ?? throw new ForbiddenException("That employee is not in your team");

    public async Task<OrgWfoOut> OrgMonthAsync(User admin, string? month, int? managerId, int? departmentId, CancellationToken ct = default)
    {
        var today = data.Today;
        var (label, start, end) = MonthBounds(month, today);
        var policy = Policies.Wfo(await Policies.GetAsync(db, ct).ConfigureAwait(false));
        var people = await scope.ResolveScopeAsync(admin, managerId, departmentId, ct: ct).ConfigureAwait(false);
        var rows = await RowsForAsync(people, start, end, today, policy, ct).ConfigureAwait(false);
        var groups = new Dictionary<int, List<WfoRow>>();
        var noManager = new List<WfoRow>();
        var order = new List<int?>();
        foreach (var r in rows)
        {
            if (r.Employee.ManagerId is { } mid)
            {
                if (!groups.TryGetValue(mid, out var list))
                {
                    groups[mid] = list = [];
                    order.Add(mid);
                }

                list.Add(r);
            }
            else
            {
                if (noManager.Count == 0)
                {
                    order.Add(null);
                }

                noManager.Add(r);
            }
        }

        var managerIds = groups.Keys.ToList();
        var managers = managerIds.Count == 0
            ? []
            : await db.Users.Include(u => u.Department).Where(u => managerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct).ConfigureAwait(false);
        var output = order.Select(mid =>
        {
            var members = mid is { } id ? groups[id] : noManager;
            var m = mid is { } key ? managers.GetValueOrDefault(key) : null;
            return new WfoManagerGroup(mid, m?.FullName ?? "No manager", m?.Department?.Name, Totals(members),
                                       [.. members.OrderBy(r => r.Employee.FullName, StringComparer.Ordinal)]);
        }).OrderBy(g => g.ManagerId is null).ThenBy(g => g.ManagerName, StringComparer.Ordinal).ToList();
        return new OrgWfoOut(label, start, end, WfoPolicyOut.Of(policy), Totals(rows), output);
    }

    [GeneratedRegex(@"^(\d{4})-(\d{2})$")]
    private static partial Regex MonthPattern();
}
