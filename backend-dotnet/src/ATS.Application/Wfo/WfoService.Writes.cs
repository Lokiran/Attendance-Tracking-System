using Ats.Application.Attendance;
using Ats.Application.Audit;
using Ats.Application.Common;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Wfo;

/// <summary>An admin's work-mode override of one day, as the API returns it.</summary>
public sealed record OverrideOut(int EmployeeId, DateOnly WorkDate, WorkMode? WorkMode, WfoSource? WfoSource, bool Overridden,
                                 string? OverrideNote, DateTimeOffset? OverriddenAt)
{
    public static OverrideOut Of(AttendanceDaily row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.EmployeeId, row.WorkDate, row.WorkMode, row.WfoSource, row.OverrideWorkMode is not null, row.OverrideNote,
                   row.OverriddenAt);
    }
}

/// <summary>
/// WFO writes (<c>app/services/wfo.py</c>): storing classification results in <c>attendance_daily</c> whenever a source
/// changes (a committed upload, an admin override), the override itself and the WFO policy. Callers own the transaction;
/// these methods save (flush) when later reads need the rows, as Python flushes.
/// </summary>
public sealed partial class WfoService
{
    private static readonly (string Key, Action<AttendancePolicy, int> Set)[] PolicyFields =
    [
        ("min_days_per_week", (p, v) => p.MinWfoDaysPerWeek = v),
        ("min_days_per_month", (p, v) => p.MinWfoDaysPerMonth = v),
        ("min_office_minutes", (p, v) => p.MinOfficeMinutesForWfoDay = v),
    ];

    private static bool HasEvidence(ClassifiedDay c)
    {
        var e = c.Evidence;
        return c.Row is not null || e.Override is not null || e.UploadMode is not null || e.UploadMinutes is not (null or 0)
               || e.BadgeMinutes is not null || e.WifiMinutes is not (null or 0);
    }

    /// <summary>Write classification results into attendance_daily (creating rows only for days with evidence).</summary>
    public int Store(EmployeeDays ed, AttendancePolicy policyRow, IReadOnlySet<DateOnly>? only = null)
    {
        ArgumentNullException.ThrowIfNull(ed);
        ArgumentNullException.ThrowIfNull(policyRow);
        var now = clock.UtcNow;
        var written = 0;
        foreach (var c in ed.Days)
        {
            if ((only is not null && !only.Contains(c.Date)) || !HasEvidence(c))
            {
                continue;
            }

            var row = c.Row ?? ed.RowsByDate.GetValueOrDefault(c.Date);
            if (row is null)
            {
                row = new AttendanceDaily { EmployeeId = ed.Employee.Id, WorkDate = c.Date, OfficeMinutes = 0, WfhMinutes = 0, Corrected = false, Conflicts = [] };
                db.AttendanceDaily.Add(row);
                ed.RowsByDate[c.Date] = row;
            }

            row.WorkMode = c.Class.WorkMode;
            row.WfoSource = c.Class.Source;
            row.Conflicts = [.. c.Class.Conflicts];
            row.OfficeMinutes = c.Class.OfficeMinutes;
            row.WfhMinutes = c.Class.WfhMinutes;
            row.Status = WfoRules.DayStatusOf(c.Class, policyRow.FullDayMinutes, policyRow.HalfDayMinutes);
            if (c.FirstIn is { } firstIn && c.LastOut is { } lastOut && lastOut >= firstIn)
            {
                (row.FirstIn, row.LastOut) = (firstIn, lastOut);
            }
            else if (c.FirstIn is { } onlyIn)
            {
                (row.FirstIn, row.LastOut) = (onlyIn, null);
            }

            row.ComputedAt = now;
            written++;
        }

        return written;
    }

    /// <summary>Recompute and store the given days for one employee (the caller commits).</summary>
    public async Task RefreshDaysAsync(User employee, IReadOnlySet<DateOnly> dates, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(dates);
        if (dates.Count == 0)
        {
            return;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var ed = (await ClassifyRangeAsync([employee], dates.Min(), dates.Max(), ct: ct).ConfigureAwait(false))[employee.Id];
        Store(ed, await Policies.GetAsync(db, ct).ConfigureAwait(false), dates);
    }

    /// <summary>Backfill (CLI <c>wfo-refresh</c>): classify and store every day of the range, 200 people at a time. The caller saves.</summary>
    public async Task<int> RefreshRangeAsync(IReadOnlyList<User> employees, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employees);
        var policyRow = await Policies.GetAsync(db, ct).ConfigureAwait(false);
        var total = 0;
        for (var i = 0; i < employees.Count; i += 200)
        {
            foreach (var ed in (await ClassifyRangeAsync([.. employees.Skip(i).Take(200)], start, end, ct: ct).ConfigureAwait(false)).Values)
            {
                total += Store(ed, policyRow);
            }
        }

        return total;
    }

    /// <summary>The day as it effectively is (freshly classified), for the audit log's old / new values.</summary>
    private async Task<Dictionary<string, object?>> OverrideSnapshotAsync(User employee, DateOnly day, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var c = (await ClassifyRangeAsync([employee], day, day, ct: ct).ConfigureAwait(false))[employee.Id].Days[0];
        return new() { ["work_mode"] = c.Class.WorkMode, ["wfo_source"] = c.Class.Source, ["override_note"] = c.Row?.OverrideNote };
    }

    private void CheckOverrideDate(User employee, DateOnly day)
    {
        if (day < employee.DateJoined)
        {
            throw new ValidationFailedException("That date is before the employee joined");
        }

        if (day > data.Today.AddDays(366))
        {
            throw new ValidationFailedException("Overrides can be set at most a year ahead");
        }
    }

    /// <summary>The employee for an admin write (any employee; 404 when missing).</summary>
    public async Task<User> EmployeeAsync(int employeeId, CancellationToken ct = default) =>
        await db.Users.FindAsync([employeeId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Employee not found");

    private Task<AttendanceDaily?> DayRowAsync(int employeeId, DateOnly day, CancellationToken ct) =>
        db.AttendanceDaily.FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.WorkDate == day, ct);

    public async Task<OverrideOut> SetOverrideAsync(User admin, User employee, DateOnly day, WorkMode mode, string? note, RequestContext ctx,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(employee);
        CheckOverrideDate(employee, day);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await DayRowAsync(employee.Id, day, ct).ConfigureAwait(false);
        var before = await OverrideSnapshotAsync(employee, day, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new AttendanceDaily
            {
                EmployeeId = employee.Id, WorkDate = day, OfficeMinutes = 0, WfhMinutes = 0, Corrected = false, Conflicts = [], Status = DailyStatus.Absent,
            };
            db.AttendanceDaily.Add(row);
        }

        row.OverrideWorkMode = mode;
        var trimmed = (note ?? "").Trim();
        row.OverrideNote = trimmed.Length > 0 ? trimmed : null;
        row.OverriddenById = admin.Id;
        row.OverriddenAt = clock.UtcNow;
        await RefreshDaysAsync(employee, new HashSet<DateOnly> { day }, ct).ConfigureAwait(false);
        AuditTrail.Record(db, admin.Id, "WORK_MODE_OVERRIDDEN", "attendance_daily", $"{employee.Id}:{day:yyyy-MM-dd}", before,
                          await OverrideSnapshotAsync(employee, day, ct).ConfigureAwait(false), ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return OverrideOut.Of(row);
    }

    public async Task<OverrideOut> ClearOverrideAsync(User admin, User employee, DateOnly day, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(employee);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await DayRowAsync(employee.Id, day, ct).ConfigureAwait(false);
        if (row?.OverrideWorkMode is null)
        {
            throw new NotFoundException("There is no override on that day");
        }

        var before = await OverrideSnapshotAsync(employee, day, ct).ConfigureAwait(false);
        (row.OverrideWorkMode, row.OverrideNote, row.OverriddenById, row.OverriddenAt) = (null, null, null, null);
        await RefreshDaysAsync(employee, new HashSet<DateOnly> { day }, ct).ConfigureAwait(false);
        AuditTrail.Record(db, admin.Id, "WORK_MODE_OVERRIDE_CLEARED", "attendance_daily", $"{employee.Id}:{day:yyyy-MM-dd}", before,
                          await OverrideSnapshotAsync(employee, day, ct).ConfigureAwait(false), ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return OverrideOut.Of(row);
    }

    private static Dictionary<string, object?> PolicyDict(WfoPolicy p) => new()
    {
        ["min_days_per_week"] = p.MinDaysPerWeek, ["min_days_per_month"] = p.MinDaysPerMonth, ["min_office_minutes"] = p.MinOfficeMinutes,
    };

    /// <summary>Change the WFO policy (fields left out or null stay as they are); audited when something changed.</summary>
    public async Task<WfoPolicyOut> UpdatePolicyAsync(User admin, IReadOnlyDictionary<string, int?> values, RequestContext ctx,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(values);
        var p = await Policies.GetAsync(db, ct).ConfigureAwait(false);
        var before = PolicyDict(Policies.Wfo(p));
        foreach (var (key, set) in PolicyFields)
        {
            if (values.GetValueOrDefault(key) is { } v)
            {
                set(p, v);
            }
        }

        p.UpdatedById = admin.Id;
        var (b, a) = AuditTrail.Changes(before, PolicyDict(Policies.Wfo(p)));
        if (a.Count > 0)
        {
            AuditTrail.Record(db, admin.Id, "WFO_POLICY_UPDATED", "attendance_policy", 1, b, a, ctx);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return WfoPolicyOut.Of(Policies.Wfo(p));
    }
}
