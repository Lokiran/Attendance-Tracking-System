using System.Globalization;
using Ats.Application.Attendance;
using Ats.Application.Common;
using Ats.Application.Data;
using Ats.Application.Scope;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Dashboard;

public sealed record ScopeArgs(int? ManagerId = null, int? DepartmentId = null);

/// <summary>
/// Team / department / organisation analytics, all derived from day records of the viewer's scope
/// (<c>app/services/dashboard.py</c>).
/// </summary>
public sealed class DashboardService(IAtsDb db, AttendanceData data, ScopeService scope, RecordCache cache, IClock clock)
{
    private static readonly (string Label, double Lo, double Hi)[] AttendanceBands =
        [("<70%", 0, 70), ("70–80%", 70, 80), ("80–90%", 80, 90), ("90–95%", 90, 95), ("95–100%", 95, 100.01)];

    public static PeriodStats Aggregate(IReadOnlyCollection<Summary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var expected = summaries.Sum(s => s.ExpectedDays);
        var presentExpected = summaries.Sum(s => s.PresentExpectedDays);
        var extra = summaries.Sum(s => s.ExtraPresentDays);
        var hoursDays = summaries.Sum(s => s.HoursDays);
        var net = summaries.Sum(s => s.NetMinutes);
        var hoursRequired = summaries.Sum(s => s.HoursRequiredMinutes);
        return new PeriodStats(expected, presentExpected, summaries.Sum(s => s.PresentDays), summaries.Sum(s => s.AbsentDays),
                               summaries.Sum(s => s.WfhDays), summaries.Sum(s => s.LeaveDays), summaries.Sum(s => s.NoTimesheetDays),
                               summaries.Sum(s => s.LateDays), net, summaries.Sum(s => s.RequiredMinutes), hoursDays, hoursRequired,
                               net - hoursRequired, hoursDays != 0 ? PyMath.RoundToInt((double)net / hoursDays) : null,
                               AttendanceRules.AttendanceRate(presentExpected + extra, expected + extra), expected + extra);
    }

    private static Snapshot SnapshotOf(IEnumerable<DayRecord?> recordsOnDate)
    {
        int present = 0, wfh = 0, absent = 0, leave = 0, noTimesheet = 0, off = 0;
        foreach (var r in recordsOnDate)
        {
            switch (r?.Status)
            {
                case null: break;
                case DayStatus.Present: present++; break;
                case DayStatus.Wfh: wfh++; break;
                case DayStatus.Absent: absent++; break;
                case DayStatus.Leave: leave++; break;
                case DayStatus.NoTimesheet: noTimesheet++; break;
                default: off++; break; // HOLIDAY, WEEKEND, NOT_SCHEDULED
            }
        }

        return new Snapshot(present, wfh, absent, leave, noTimesheet, off);
    }

    /// <summary>Day records of a scope plus what every dashboard endpoint derives from them.</summary>
    private sealed record Computed(Dictionary<int, List<DayRecord>> Period, Dictionary<int, DayRecord?> On,
                                   Dictionary<int, Summary> Summary, Dictionary<int, int> ExceptionCount);

    private static Computed Derive(IReadOnlyDictionary<int, IReadOnlyList<DayRecord>> all, DateOnly start, DateOnly end, DateOnly? onDate)
    {
        var period = all.ToDictionary(kv => kv.Key, kv => kv.Value.Where(r => start <= r.Date && r.Date <= end).ToList());
        var on = onDate is { } day ? all.ToDictionary(kv => kv.Key, kv => kv.Value.FirstOrDefault(r => r.Date == day)) : [];
        return new Computed(period, on, period.ToDictionary(kv => kv.Key, kv => AttendanceRules.Summarize(kv.Value, start, end)),
                            period.ToDictionary(kv => kv.Key, kv => kv.Value.Sum(r => r.Exceptions.Count)));
    }

    private async Task<Computed> ComputeAsync(IReadOnlyList<User> employees, DateOnly start, DateOnly end, DateOnly? onDate, CancellationToken ct)
    {
        var lo = onDate is { } a && a < start ? a : start;
        var hi = onDate is { } b && b > end ? b : end;
        var records = await data.ComputeRecordsAsync(employees, lo, hi, ct).ConfigureAwait(false);
        if (records.CacheKey is null)
        {
            return Derive(records.ByEmployee, start, end, onDate);
        }

        // The page's requests share the cached records; derive the summaries from them once as well.
        return await cache.GetOrComputeAsync($"derived|{records.CacheKey}|{start.DayNumber}|{end.DayNumber}|{onDate?.DayNumber}",
                                             records.ByEmployee.Count, () => Task.FromResult(Derive(records.ByEmployee, start, end, onDate)))
            .ConfigureAwait(false);
    }

    public async Task<OverviewOut> OverviewAsync(User viewer, ScopeArgs args, DateOnly start, DateOnly end, DateOnly onDate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var employees = await scope.ResolveScopeAsync(viewer, args.ManagerId, args.DepartmentId, ct: ct).ConfigureAwait(false);
        var c = await ComputeAsync(employees, start, end, onDate, ct).ConfigureAwait(false);
        var summaries = employees.Select(e => c.Summary[e.Id]).ToList();
        return new OverviewOut(clock.UtcNow, onDate, start, end, employees.Count, SnapshotOf(employees.Select(e => c.On.GetValueOrDefault(e.Id))),
                               Aggregate(summaries), Bands(summaries), await Timesheets.LatestImportThroughAsync(db, ct).ConfigureAwait(false));
    }

    /// <summary>How many people fall in each attendance band, over the whole scope (never a sample).</summary>
    public static List<BandCount> Bands(IEnumerable<Summary> summaries)
    {
        var counts = new int[AttendanceBands.Length];
        foreach (var s in summaries)
        {
            if (s.AttendancePercentage is not { } pct)
            {
                continue;
            }

            for (var i = 0; i < AttendanceBands.Length; i++)
            {
                if (AttendanceBands[i].Lo <= pct && pct < AttendanceBands[i].Hi)
                {
                    counts[i]++;
                    break;
                }
            }
        }

        return [.. AttendanceBands.Select((b, i) => new BandCount(b.Label, counts[i]))];
    }

    public async Task<EmployeeRowsPage> EmployeeRowsAsync(User viewer, ScopeArgs args, DateOnly start, DateOnly end, DateOnly onDate, string? search,
                                                          DayStatus? status, string sort, string order, int page, int pageSize,
                                                          bool includeInactive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var employees = await scope.ResolveScopeAsync(viewer, args.ManagerId, args.DepartmentId, includeInactive, ct: ct).ConfigureAwait(false);
        // Compute for the whole scope (shared with the page's other requests), then filter.
        var c = await ComputeAsync(employees, start, end, onDate, ct).ConfigureAwait(false);
        IEnumerable<User> filtered = employees;
        if (!string.IsNullOrEmpty(search))
        {
            var q = search.Trim().ToLowerInvariant();
            filtered = employees.Where(e => e.FullName.ToLowerInvariant().Contains(q, StringComparison.Ordinal)
                                            || e.EmployeeCode.ToLowerInvariant().Contains(q, StringComparison.Ordinal));
        }

        var rows = new List<EmployeeRow>();
        foreach (var e in filtered)
        {
            var day = c.On.GetValueOrDefault(e.Id);
            if (status is { } wanted && (day is null || day.Status != wanted))
            {
                continue;
            }

            rows.Add(new EmployeeRow(e.Id, e.EmployeeCode, e.FullName, e.JobTitle, e.Role, e.IsActive, e.DepartmentId, e.Department?.Name,
                                     e.ManagerId, e.Manager?.FullName,
                                     new RowDay(day?.Status, day?.FirstIn, day?.LastOut, day?.NetMinutes ?? 0, day?.HoursKnown ?? false, day?.WorkMode,
                                                day is null ? [] : [.. day.Exceptions]),
                                     Aggregate([c.Summary[e.Id]]), c.ExceptionCount[e.Id]));
        }

        var sorted = Sort(rows, sort, order == "desc");
        var offset = (page - 1) * pageSize;
        return new EmployeeRowsPage([.. sorted.Skip(offset).Take(pageSize)], rows.Count, page, pageSize);
    }

    /// <summary>
    /// The employee table's sort keys (SORT_KEYS), stable as Python's sort. TODO(parity) P1: the route accepts
    /// <c>check_in</c>, which the service does not know (its key is <c>first_in</c>), so it sorts by name.
    /// </summary>
    private static List<EmployeeRow> Sort(List<EmployeeRow> rows, string sort, bool descending)
    {
        static IOrderedEnumerable<EmployeeRow> By<TKey>(List<EmployeeRow> r, Func<EmployeeRow, TKey> key, bool desc, IComparer<TKey>? comparer = null) =>
            desc ? r.OrderByDescending(key, comparer) : r.OrderBy(key, comparer);

        // (value is None, value or 0): missing values after present ones in ascending order.
        static IOrderedEnumerable<EmployeeRow> NullLast<T>(List<EmployeeRow> r, Func<EmployeeRow, T?> key, bool desc)
            where T : struct, IComparable<T> =>
            desc
                ? r.OrderByDescending(x => key(x) is null).ThenByDescending(x => key(x) ?? default)
                : r.OrderBy(x => key(x) is null).ThenBy(x => key(x) ?? default);

        return sort switch
        {
            "code" => [.. By(rows, r => r.EmployeeCode, descending, StringComparer.Ordinal)],
            "department" => [.. By(rows, r => (r.DepartmentName ?? "").ToLowerInvariant(), descending, StringComparer.Ordinal)],
            "net_minutes" => [.. By(rows, r => r.Period.NetMinutes, descending)],
            "average_net_minutes" => [.. NullLast(rows, r => r.Period.AverageNetMinutes, descending)],
            "attendance_percentage" => [.. NullLast(rows, r => r.Period.AttendancePercentage, descending)],
            "status" => [.. By(rows, r => r.Day.Status is { } s ? EnumText.Of(s) : "", descending, StringComparer.Ordinal)],
            "exceptions" => [.. By(rows, r => r.ExceptionCount, descending)],
            _ => [.. By(rows, r => r.FullName.ToLowerInvariant(), descending, StringComparer.Ordinal)], // name, and P1's check_in
        };
    }

    private static (DateOnly Start, DateOnly End, string Label) BucketKey(DateOnly d, string granularity)
    {
        switch (granularity)
        {
            case "week":
                var s = d.AddDays(-OfficeTime.Weekday(d));
                return (s, s.AddDays(6), $"Wk {ISOWeek.GetWeekOfYear(s.ToDateTime(TimeOnly.MinValue)).ToString(CultureInfo.InvariantCulture)} ({WfoRules.DayMonth(s)})");
            case "month":
                var m = new DateOnly(d.Year, d.Month, 1);
                return (m, OfficeTime.MonthEnd(m), m.ToString("MMM yyyy", CultureInfo.InvariantCulture));
            default:
                return (d, d, WfoRules.DayMonth(d));
        }
    }

    private sealed class Bucket(DateOnly start, DateOnly end, string label)
    {
        public DateOnly Start { get; } = start;
        public DateOnly End { get; } = end;
        public string Label { get; } = label;
        public int Expected, PresentExpected, Present, Absent, Wfh, NetMinutes, Attended, RequiredMinutes, HoursRequiredMinutes, Late, ShortHours;
    }

    /// <summary>Day / week / month buckets (team and employee trends). TODO(parity) P4: end is clipped to the range, start is not.</summary>
    public static List<TrendBucket> Bucketize(IEnumerable<DayRecord> records, DateOnly end, string granularity)
    {
        ArgumentNullException.ThrowIfNull(records);
        var buckets = new Dictionary<DateOnly, Bucket>();
        var bucketOf = new Dictionary<DateOnly, Bucket>();
        foreach (var r in records)
        {
            if (!bucketOf.TryGetValue(r.Date, out var b))
            {
                var (bStart, bEnd, label) = BucketKey(r.Date, granularity);
                if (!buckets.TryGetValue(bStart, out b))
                {
                    buckets[bStart] = b = new Bucket(bStart, bEnd < end ? bEnd : end, label);
                }

                bucketOf[r.Date] = b;
            }

            if (r.Expected)
            {
                b.Expected++;
                if (r.Status == DayStatus.Present)
                {
                    b.PresentExpected++;
                }
            }

            if (r.Status == DayStatus.Present)
            {
                b.Present++;
                if (!r.Expected)
                {
                    // In the office on a holiday / off day: counts as a working day attended.
                    b.Expected++;
                    b.PresentExpected++;
                }

                if (r.HoursKnown)
                {
                    b.Attended++; // days with recorded hours (the average's denominator)
                    b.HoursRequiredMinutes += r.RequiredMinutes;
                }
            }
            else if (r.Status == DayStatus.Absent)
            {
                b.Absent++;
            }
            else if (r.Status == DayStatus.Wfh)
            {
                b.Wfh++;
            }

            b.NetMinutes += r.NetMinutes;
            b.RequiredMinutes += r.RequiredMinutes;
            if (r.Exceptions.Count > 0)
            {
                b.Late += r.Exceptions.Contains(Exceptions.LateArrival) ? 1 : 0;
                b.ShortHours += r.Exceptions.Contains(Exceptions.ShortHours) ? 1 : 0;
            }
        }

        return [.. buckets.Values.OrderBy(b => b.Start).Select(b => new TrendBucket(
            b.Start, b.End, b.Label, b.Expected, b.PresentExpected, b.Present, b.Absent, b.Wfh, b.NetMinutes, b.RequiredMinutes,
            b.HoursRequiredMinutes, b.Attended, b.Late, b.ShortHours,
            b.Expected != 0 ? PyMath.Round(b.PresentExpected * 100.0 / b.Expected, 1) : null,
            b.Attended != 0 ? PyMath.RoundToInt((double)b.NetMinutes / b.Attended) : null))];
    }

    public async Task<TrendsOut> TrendsAsync(User viewer, ScopeArgs args, DateOnly start, DateOnly end, string granularity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var employees = await scope.ResolveScopeAsync(viewer, args.ManagerId, args.DepartmentId, ct: ct).ConfigureAwait(false);
        var c = await ComputeAsync(employees, start, end, null, ct).ConfigureAwait(false);
        return new TrendsOut(granularity, employees.Count, Bucketize(c.Period.Values.SelectMany(r => r), end, granularity));
    }

    public async Task<ExceptionsOut> ExceptionsAsync(User viewer, ScopeArgs args, DateOnly start, DateOnly end, string? exceptionType, int page,
                                                     int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var employees = await scope.ResolveScopeAsync(viewer, args.ManagerId, args.DepartmentId, ct: ct).ConfigureAwait(false);
        var period = (await ComputeAsync(employees, start, end, null, ct).ConfigureAwait(false)).Period;
        var byId = employees.ToDictionary(e => e.Id);
        var counts = new Dictionary<string, int>();
        var matches = new List<(DateOnly Date, string Name, DayRecord Record)>();
        foreach (var (eid, recs) in period)
        {
            var name = byId[eid].FullName;
            foreach (var r in recs.Where(r => r.Exceptions.Count > 0))
            {
                foreach (var x in r.Exceptions)
                {
                    counts[x] = counts.GetValueOrDefault(x) + 1;
                }

                if (!string.IsNullOrEmpty(exceptionType) && !r.Exceptions.Contains(exceptionType))
                {
                    continue;
                }

                matches.Add((r.Date, name, r));
            }
        }

        var ordered = matches.OrderByDescending(m => m.Date).ThenByDescending(m => m.Name, StringComparer.Ordinal).ToList();
        var rows = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(m =>
        {
            var e = byId[m.Record.EmployeeId];
            var r = m.Record;
            return new ExceptionRow(e.Id, e.EmployeeCode, e.FullName, e.Department?.Name, r.Date, r.Status, [.. r.Exceptions], r.FirstIn,
                                    r.LastOut, r.NetMinutes, r.HoursKnown, r.RequiredMinutes);
        }).ToList();
        return new ExceptionsOut(counts, rows, matches.Count, page, pageSize);
    }

    /// <summary>Every manager's whole subtree, from (user id, manager id) pairs (cycle-safe).</summary>
    private static Dictionary<int, HashSet<int>> ReportsTree(IEnumerable<(int Id, int? ManagerId)> links)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var (uid, managerId) in links)
        {
            if (managerId is { } m)
            {
                if (!children.TryGetValue(m, out var list))
                {
                    children[m] = list = [];
                }

                list.Add(uid);
            }
        }

        HashSet<int> Collect(int root)
        {
            var output = new HashSet<int>();
            var stack = new Stack<int>(children.GetValueOrDefault(root) ?? []);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n == root || !output.Add(n))
                {
                    continue;
                }

                foreach (var c in children.GetValueOrDefault(n) ?? [])
                {
                    stack.Push(c);
                }
            }

            return output;
        }

        return children.Keys.ToDictionary(root => root, Collect);
    }

    public async Task<List<DepartmentStats>> DepartmentComparisonAsync(User viewer, DateOnly start, DateOnly end, DateOnly onDate, CancellationToken ct = default)
    {
        var employees = await scope.ResolveScopeAsync(viewer, ct: ct).ConfigureAwait(false);
        var c = await ComputeAsync(employees, start, end, onDate, ct).ConfigureAwait(false);
        var departments = await db.Departments.Include(d => d.Head).OrderBy(d => d.Name).ToListAsync(ct).ConfigureAwait(false);
        var output = new List<DepartmentStats>();
        foreach (var dept in departments.Cast<Department?>().Append(null))
        {
            var members = employees.Where(e => e.DepartmentId == dept?.Id).ToList();
            if (dept is null && members.Count == 0)
            {
                continue;
            }

            output.Add(new DepartmentStats(dept?.Id, dept?.Name ?? "Unassigned", dept?.Head?.FullName, members.Count,
                                           SnapshotOf(members.Select(m => c.On.GetValueOrDefault(m.Id))),
                                           Aggregate([.. members.Select(m => c.Summary[m.Id])])));
        }

        return output;
    }

    public async Task<List<ManagerStats>> ManagerComparisonAsync(User viewer, DateOnly start, DateOnly end, DateOnly onDate, int? departmentId,
                                                                 CancellationToken ct = default)
    {
        var employees = await scope.ResolveScopeAsync(viewer, ct: ct).ConfigureAwait(false);
        // Only (id, manager id) pairs for everyone; full rows just for the people who manage someone.
        var links = await db.Users.AsNoTracking().Select(u => new { u.Id, u.ManagerId }).ToListAsync(ct).ConfigureAwait(false);
        var tree = ReportsTree(links.Select(l => (l.Id, l.ManagerId)));
        var c = await ComputeAsync(employees, start, end, onDate, ct).ConfigureAwait(false);
        var scoped = employees.ToDictionary(e => e.Id);
        var rootIds = tree.Keys.ToList();
        var managers = rootIds.Count == 0
            ? []
            : await db.Users.Include(u => u.Department).Where(u => rootIds.Contains(u.Id) && u.Role != Role.Employee).ToListAsync(ct).ConfigureAwait(false);
        var output = new List<ManagerStats>();
        foreach (var u in managers)
        {
            if (departmentId is { } d && u.DepartmentId != d)
            {
                continue;
            }

            var members = tree[u.Id].Where(scoped.ContainsKey).Select(i => scoped[i]).ToList();
            output.Add(new ManagerStats(u.Id, u.FullName, u.EmployeeCode, u.Department?.Name, members.Count, members.Count(m => m.ManagerId == u.Id),
                                        SnapshotOf(members.Select(m => c.On.GetValueOrDefault(m.Id))),
                                        Aggregate([.. members.Select(m => c.Summary[m.Id])])));
        }

        return [.. output.OrderBy(r => r.ManagerName, StringComparer.Ordinal)];
    }

    // ------------------------------------------------------------------ organisation and team

    /// <summary>Organisation -> department -> top-of-department people -> reporting tree (<c>app/services/org.py</c>).</summary>
    public async Task<HierarchyOut> HierarchyAsync(bool includeInactive, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive);
        }

        var users = await query.OrderBy(u => u.FullName).ToListAsync(ct).ConfigureAwait(false);
        var byId = users.ToDictionary(u => u.Id);
        var children = new Dictionary<int, List<User>>();
        foreach (var u in users.Where(u => u.ManagerId is { } m && byId.ContainsKey(m)))
        {
            if (!children.TryGetValue(u.ManagerId!.Value, out var list))
            {
                children[u.ManagerId.Value] = list = [];
            }

            list.Add(u);
        }

        HierarchyNode Node(User u, HashSet<int> seen)
        {
            seen.Add(u.Id);
            var all = children.GetValueOrDefault(u.Id) ?? [];
            var kids = all.Where(k => k.DepartmentId == u.DepartmentId && !seen.Contains(k.Id)).OrderBy(k => k.FullName, StringComparer.Ordinal).ToList();
            var nodes = new List<HierarchyNode>();
            foreach (var k in kids)
            {
                nodes.Add(Node(k, seen));
            }

            return new HierarchyNode(u.Id, u.EmployeeCode, u.FullName, u.Role, u.JobTitle, u.IsActive, all.Count, nodes);
        }

        var departments = await db.Departments.AsNoTracking().Include(d => d.Head).OrderBy(d => d.Name).ToListAsync(ct).ConfigureAwait(false);
        var output = new List<HierarchyDepartment>();
        foreach (var dept in departments.Cast<Department?>().Append(null))
        {
            var deptId = dept?.Id;
            var members = users.Where(u => u.DepartmentId == deptId).ToList();
            if (dept is null && members.Count == 0)
            {
                continue;
            }

            // Roots: members whose manager is absent or sits in another department (cross-department reporting).
            var roots = members.Where(u => u.ManagerId is not { } m || !byId.TryGetValue(m, out var boss) || boss.DepartmentId != deptId).ToList();
            var seen = new HashSet<int>();
            var nodes = new List<HierarchyNode>();
            foreach (var r in roots)
            {
                nodes.Add(Node(r, seen));
            }

            output.Add(new HierarchyDepartment(deptId, dept?.Name ?? "Unassigned", dept?.Description,
                                               dept?.Head is { } head ? new HeadBrief(head.Id, head.FullName) : null, members.Count, nodes));
        }

        return new HierarchyOut(users.Count, output);
    }

    public static TeamMember Member(User p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new TeamMember(p.Id, p.EmployeeCode, p.FullName, p.Role, p.JobTitle, p.Department?.Name, p.ManagerId, p.Manager?.FullName, p.IsActive);
    }
}
