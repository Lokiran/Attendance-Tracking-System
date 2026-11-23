using Ats.Application.Attendance;
using Ats.Application.Audit;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Application.Reference;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Admin;

/// <summary>
/// Work schedules, holidays, the attendance policy, the audit log and the automatic-attendance settings
/// (<c>app/services/configuration.py</c>, <c>auto_settings.py</c>, the audit list of <c>routes/admin.py</c>).
/// </summary>
public sealed class ConfigurationService(IAtsDb db, AtsSettings settings)
{
    private static readonly string[] PolicyFields =
        ["min_present_minutes", "count_incomplete_as_present", "max_session_minutes", "late_grace_minutes", "correction_window_days",
         "manager_can_approve", "notes"];

    private static readonly string[] AutoFields =
        ["auto_attendance_enabled", "auto_shadow_mode", "gap_minutes", "full_day_minutes", "half_day_minutes", "wifi_poll_minutes",
         "presence_poll_minutes", "no_data_alert_minutes", "raw_retention_days", "wfh_presence_states"];

    // ---------------------------------------------------------------- schedules

    private static Dictionary<string, object?> ScheduleSnapshot(WorkSchedule s) => new()
    {
        ["name"] = s.Name, ["employee_id"] = s.EmployeeId, ["department_id"] = s.DepartmentId, ["start_time"] = s.StartTime,
        ["end_time"] = s.EndTime, ["required_minutes"] = s.RequiredMinutes, ["working_days"] = s.WorkingDays,
        ["effective_from"] = s.EffectiveFrom, ["effective_to"] = s.EffectiveTo,
    };

    private async Task<ScheduleOut> ScheduleOutAsync(WorkSchedule s, CancellationToken ct)
    {
        string label;
        if (s.EmployeeId is { } eid)
        {
            var u = await db.Users.FindAsync([eid], ct).ConfigureAwait(false);
            label = u is not null ? $"Employee: {u.FullName}" : "Employee";
        }
        else if (s.DepartmentId is { } did)
        {
            var d = await db.Departments.FindAsync([did], ct).ConfigureAwait(false);
            label = d is not null ? $"Department: {d.Name}" : "Department";
        }
        else
        {
            label = "Organisation default";
        }

        return new ScheduleOut(s.Id, s.Name, s.EmployeeId, s.DepartmentId, s.StartTime, s.EndTime, s.RequiredMinutes, [.. s.WorkingDays],
                               s.EffectiveFrom, s.EffectiveTo, label);
    }

    public async Task<List<ScheduleOut>> SchedulesAsync(CancellationToken ct = default)
    {
        var rows = await db.WorkSchedules.OrderBy(s => s.EmployeeId != null).ThenBy(s => s.DepartmentId != null)
            .ThenByDescending(s => s.EffectiveFrom).ToListAsync(ct).ConfigureAwait(false);
        var output = new List<ScheduleOut>(rows.Count);
        foreach (var s in rows)
        {
            output.Add(await ScheduleOutAsync(s, ct).ConfigureAwait(false));
        }

        return output;
    }

    /// <summary>The fields of a schedule being created or updated (merged with the current values for an update).</summary>
    public sealed record ScheduleData(string Name, int? EmployeeId, int? DepartmentId, TimeOnly StartTime, TimeOnly EndTime, int RequiredMinutes,
                                      List<int> WorkingDays, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

    private async Task ValidateScheduleAsync(ScheduleData data, int? scheduleId, CancellationToken ct)
    {
        if (data.EmployeeId is not null && data.DepartmentId is not null)
        {
            throw new ValidationFailedException("A schedule applies to an employee, a department, or the whole organisation");
        }

        if (data.EmployeeId is { } eid && await db.Users.FindAsync([eid], ct).ConfigureAwait(false) is null)
        {
            throw new ValidationFailedException("Employee does not exist");
        }

        if (data.DepartmentId is { } did && await db.Departments.FindAsync([did], ct).ConfigureAwait(false) is null)
        {
            throw new ValidationFailedException("Department does not exist");
        }

        var days = data.WorkingDays;
        if (days.Count == 0 || days.Any(d => d is < 1 or > 7) || days.Distinct().Count() != days.Count)
        {
            throw new ValidationFailedException("Working days must be distinct ISO weekday numbers between 1 (Mon) and 7 (Sun)");
        }

        if (data.RequiredMinutes is < 1 or > 1440)
        {
            throw new ValidationFailedException("Required minutes must be between 1 and 1440");
        }

        if (data.StartTime == data.EndTime)
        {
            throw new ValidationFailedException("Start and end time cannot be equal");
        }

        if (data.EffectiveTo is { } to && to < data.EffectiveFrom)
        {
            throw new ValidationFailedException("Effective end date must be on or after the start date");
        }

        // Two assignments for the same target must not be effective at the same time.
        var others = await db.WorkSchedules.Where(s => s.EmployeeId == data.EmployeeId && s.DepartmentId == data.DepartmentId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var other in others.Where(o => o.Id != scheduleId))
        {
            var otherEnd = other.EffectiveTo ?? DateOnly.MaxValue;
            if (other.EffectiveFrom <= (data.EffectiveTo ?? DateOnly.MaxValue) && data.EffectiveFrom <= otherEnd)
            {
                throw new ConflictException($"Overlaps the effective dates of schedule '{other.Name}'");
            }
        }
    }

    public async Task<ScheduleOut> CreateScheduleAsync(User actor, ScheduleData data, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(data);
        data = data with { WorkingDays = [.. data.WorkingDays.Order()] };
        await ValidateScheduleAsync(data, null, ct).ConfigureAwait(false);
        var s = new WorkSchedule
        {
            Name = data.Name, EmployeeId = data.EmployeeId, DepartmentId = data.DepartmentId, StartTime = data.StartTime, EndTime = data.EndTime,
            RequiredMinutes = data.RequiredMinutes, WorkingDays = data.WorkingDays, EffectiveFrom = data.EffectiveFrom, EffectiveTo = data.EffectiveTo,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.WorkSchedules.Add(s);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        AuditTrail.Record(db, actor.Id, "SCHEDULE_CREATED", "work_schedule", s.Id, after: ScheduleSnapshot(s), ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return await ScheduleOutAsync(s, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Update with the sent fields. TODO(parity) P12: Python fails with a server error when a required field (name, times,
    /// minutes, working days, start date) is sent as null; here such a null keeps the current value.
    /// </summary>
    public async Task<ScheduleOut> UpdateScheduleAsync(User actor, int scheduleId, IReadOnlyDictionary<string, object?> changes, RequestContext ctx,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(changes);
        var s = await db.WorkSchedules.FindAsync([scheduleId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Schedule not found");
        var before = ScheduleSnapshot(s);
        T Pick<T>(string key, T current) => changes.GetValueOrDefault(key) is T v ? v : current;
        var merged = new ScheduleData(
            Pick("name", s.Name), s.EmployeeId, s.DepartmentId, Pick("start_time", s.StartTime), Pick("end_time", s.EndTime),
            Pick("required_minutes", s.RequiredMinutes), [.. Pick("working_days", s.WorkingDays).Order()], Pick("effective_from", s.EffectiveFrom),
            changes.TryGetValue("effective_to", out var to) ? (DateOnly?)to : s.EffectiveTo);
        await ValidateScheduleAsync(merged, s.Id, ct).ConfigureAwait(false);
        (s.Name, s.StartTime, s.EndTime, s.RequiredMinutes) = (merged.Name, merged.StartTime, merged.EndTime, merged.RequiredMinutes);
        (s.WorkingDays, s.EffectiveFrom, s.EffectiveTo) = (merged.WorkingDays, merged.EffectiveFrom, merged.EffectiveTo);
        var (b, a) = AuditTrail.Changes(before, ScheduleSnapshot(s));
        AuditTrail.Record(db, actor.Id, "SCHEDULE_UPDATED", "work_schedule", s.Id, b, a, ctx); // recorded even when nothing changed, as in Python
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await ScheduleOutAsync(s, ct).ConfigureAwait(false);
    }

    public async Task DeleteScheduleAsync(User actor, int scheduleId, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var s = await db.WorkSchedules.FindAsync([scheduleId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Schedule not found");
        AuditTrail.Record(db, actor.Id, "SCHEDULE_DELETED", "work_schedule", s.Id, before: ScheduleSnapshot(s), ctx: ctx);
        db.WorkSchedules.Remove(s);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- holidays

    private static Dictionary<string, object?> HolidaySnapshot(Holiday h) => new()
    {
        ["name"] = h.Name, ["holiday_date"] = h.HolidayDate, ["department_id"] = h.DepartmentId,
    };

    public async Task<HolidayOut> CreateHolidayAsync(User actor, string name, DateOnly date, int? departmentId, RequestContext ctx,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(name);
        if (departmentId is { } did && await db.Departments.FindAsync([did], ct).ConfigureAwait(false) is null)
        {
            throw new ValidationFailedException("Department does not exist");
        }

        if (await db.Holidays.AnyAsync(h => h.HolidayDate == date && h.DepartmentId == departmentId, ct).ConfigureAwait(false))
        {
            throw new ConflictException("A holiday already exists on this date for the same scope");
        }

        var holiday = new Holiday { Name = Uploads.PyText.Strip(name), HolidayDate = date, DepartmentId = departmentId };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Holidays.Add(holiday);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        AuditTrail.Record(db, actor.Id, "HOLIDAY_CREATED", "holiday", holiday.Id, after: HolidaySnapshot(holiday), ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new HolidayOut(holiday.Id, holiday.Name, holiday.HolidayDate, holiday.DepartmentId);
    }

    public async Task DeleteHolidayAsync(User actor, int holidayId, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var h = await db.Holidays.FindAsync([holidayId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Holiday not found");
        AuditTrail.Record(db, actor.Id, "HOLIDAY_DELETED", "holiday", h.Id, before: HolidaySnapshot(h), ctx: ctx);
        db.Holidays.Remove(h);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- policy

    private static Dictionary<string, object?> PolicySnapshot(AttendancePolicy p) => new()
    {
        ["min_present_minutes"] = p.MinPresentMinutes, ["count_incomplete_as_present"] = p.CountIncompleteAsPresent,
        ["max_session_minutes"] = p.MaxSessionMinutes, ["late_grace_minutes"] = p.LateGraceMinutes,
        ["correction_window_days"] = p.CorrectionWindowDays, ["manager_can_approve"] = p.ManagerCanApprove, ["notes"] = p.Notes,
    };

    /// <summary>Change the attendance policy: fields left out or null keep their value; audited when something changed.</summary>
    public async Task<PolicyOut> UpdatePolicyAsync(User actor, IReadOnlyDictionary<string, object?> data, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(data);
        var p = await Policies.GetAsync(db, ct).ConfigureAwait(false);
        var before = PolicySnapshot(p);
        foreach (var f in PolicyFields)
        {
            switch (data.GetValueOrDefault(f))
            {
                case null:
                    break;
                case int i when f == "min_present_minutes":
                    p.MinPresentMinutes = i;
                    break;
                case int i when f == "max_session_minutes":
                    p.MaxSessionMinutes = i;
                    break;
                case int i when f == "late_grace_minutes":
                    p.LateGraceMinutes = i;
                    break;
                case int i when f == "correction_window_days":
                    p.CorrectionWindowDays = i;
                    break;
                case bool b when f == "count_incomplete_as_present":
                    p.CountIncompleteAsPresent = b;
                    break;
                case bool b when f == "manager_can_approve":
                    p.ManagerCanApprove = b;
                    break;
                case string s when f == "notes":
                    p.Notes = s;
                    break;
            }
        }

        p.UpdatedById = actor.Id;
        var (bv, av) = AuditTrail.Changes(before, PolicySnapshot(p));
        if (av.Count > 0)
        {
            AuditTrail.Record(db, actor.Id, "POLICY_UPDATED", "attendance_policy", 1, bv, av, ctx);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return PolicyOut.Of(p);
    }

    // ---------------------------------------------------------------- automatic attendance settings

    private static Dictionary<string, object?> AutoSnapshot(AttendancePolicy p) => new()
    {
        ["auto_attendance_enabled"] = p.AutoAttendanceEnabled, ["auto_shadow_mode"] = p.AutoShadowMode, ["gap_minutes"] = p.GapMinutes,
        ["full_day_minutes"] = p.FullDayMinutes, ["half_day_minutes"] = p.HalfDayMinutes, ["wifi_poll_minutes"] = p.WifiPollMinutes,
        ["presence_poll_minutes"] = p.PresencePollMinutes, ["no_data_alert_minutes"] = p.NoDataAlertMinutes,
        ["raw_retention_days"] = p.RawRetentionDays, ["wfh_presence_states"] = p.WfhPresenceStates,
    };

    public async Task<AutoAttendanceSettingsOut> AutoSettingsAsync(CancellationToken ct = default) =>
        AutoAttendanceSettingsOut.Of(await Policies.GetAsync(db, ct).ConfigureAwait(false));

    /// <summary>Partial update; the half-day rule is checked on the merged values, so a partial update cannot break it.</summary>
    public async Task<AutoAttendanceSettingsOut> UpdateAutoSettingsAsync(User actor, IReadOnlyDictionary<string, object?> data, RequestContext ctx,
                                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(data);
        var p = await Policies.GetAsync(db, ct).ConfigureAwait(false);
        var before = AutoSnapshot(p);
        var merged = new Dictionary<string, object?>(before);
        foreach (var f in AutoFields)
        {
            if (data.GetValueOrDefault(f) is { } v)
            {
                merged[f] = v;
            }
        }

        if ((int)merged["half_day_minutes"]! > (int)merged["full_day_minutes"]!)
        {
            throw new ValidationFailedException("Half-day minutes cannot be more than full-day minutes");
        }

        p.AutoAttendanceEnabled = (bool)merged["auto_attendance_enabled"]!;
        p.AutoShadowMode = (bool)merged["auto_shadow_mode"]!;
        p.GapMinutes = (int)merged["gap_minutes"]!;
        p.FullDayMinutes = (int)merged["full_day_minutes"]!;
        p.HalfDayMinutes = (int)merged["half_day_minutes"]!;
        p.WifiPollMinutes = (int)merged["wifi_poll_minutes"]!;
        p.PresencePollMinutes = (int)merged["presence_poll_minutes"]!;
        p.NoDataAlertMinutes = (int)merged["no_data_alert_minutes"]!;
        p.RawRetentionDays = (int)merged["raw_retention_days"]!;
        p.WfhPresenceStates = [.. (List<string>)merged["wfh_presence_states"]!];
        p.UpdatedById = actor.Id;
        var (b, a) = AuditTrail.Changes(before, AutoSnapshot(p));
        if (a.Count > 0)
        {
            AuditTrail.Record(db, actor.Id, "AUTO_ATTENDANCE_SETTINGS_UPDATED", "attendance_policy", 1, b, a, ctx);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return AutoAttendanceSettingsOut.Of(p);
    }

    // ---------------------------------------------------------------- audit log

    public async Task<PagedResult<AuditOut>> AuditLogsAsync(string? action, string? entityType, int? actorId, string? search, DateOnly? start,
                                                     DateOnly? end, int page, int pageSize, CancellationToken ct = default)
    {
        var query = from log in db.AuditLogs.AsNoTracking()
                    join u in db.Users on log.ActorUserId equals u.Id into actors
                    from actor in actors.DefaultIfEmpty()
                    select new { log, ActorName = actor == null ? null : actor.FullName };
        if (!string.IsNullOrEmpty(action))
        {
            query = query.Where(x => x.log.Action == action);
        }

        if (!string.IsNullOrEmpty(entityType))
        {
            query = query.Where(x => x.log.EntityType == entityType);
        }

        if (actorId is { } aid)
        {
            query = query.Where(x => x.log.ActorUserId == aid);
        }

        if (!string.IsNullOrEmpty(search))
        {
            var like = $"%{search.Trim().ToLowerInvariant()}%";
            var exact = search.Trim();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(), as in Python
            query = query.Where(x => EF.Functions.Like(x.log.Action.ToLower(), like) || EF.Functions.Like(x.ActorName!.ToLower(), like)
                                     || x.log.EntityId == exact);
#pragma warning restore CA1304, CA1311, CA1862
        }

        if (start is { } s)
        {
            var from = OfficeTime.ToUtc(s, TimeOnly.MinValue, settings.TimeZone);
            query = query.Where(x => x.log.CreatedAt >= from);
        }

        if (end is { } e)
        {
            var to = OfficeTime.ToUtc(e.AddDays(1), TimeOnly.MinValue, settings.TimeZone);
            query = query.Where(x => x.log.CreatedAt < to);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderByDescending(x => x.log.CreatedAt).ThenByDescending(x => x.log.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<AuditOut>([.. rows.Select(x => new AuditOut(x.log.Id, x.log.ActorUserId, x.ActorName, x.log.Action, x.log.EntityType,
                                                                     x.log.EntityId, x.log.BeforeValues, x.log.AfterValues, x.log.RequestId,
                                                                     x.log.IpAddress, x.log.CreatedAt))], total, page, pageSize);
    }
}
