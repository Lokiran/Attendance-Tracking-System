using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Application.Wfo;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Attendance;

/// <summary>
/// Per-employee attendance queries (<c>app/services/attendance_service.py</c>). Attendance comes from the timesheets the
/// Admin imports; there is no check-in / check-out.
/// </summary>
public sealed class AttendanceService(IAtsDb db, AttendanceData data, WfoService wfo, IClock clock, AtsSettings settings)
{
    public async Task<IReadOnlyList<DayRecord>> RecordsForAsync(User employee, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);
        return (await data.ComputeRecordsAsync([employee], start, end, ct).ConfigureAwait(false)).ByEmployee[employee.Id];
    }

    public async Task<(Summary Summary, IReadOnlyList<DayRecord> Records)> SummaryForAsync(User employee, DateOnly start, DateOnly end,
                                                                                          CancellationToken ct = default)
    {
        var records = await RecordsForAsync(employee, start, end, ct).ConfigureAwait(false);
        return (AttendanceRules.Summarize(records, start, end), records);
    }

    private async Task<List<ScheduleData>> AllSchedulesAsync(CancellationToken ct) =>
        (await db.WorkSchedules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).Select(AttendanceData.ScheduleDataOf).ToList();

    /// <summary>Today, the shift, and how far the imported timesheets reach.</summary>
    public async Task<CurrentStatusOut> CurrentStatusAsync(User employee, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);
        var now = clock.UtcNow;
        var today = OfficeTime.LocalDate(now, settings.TimeZone);
        var schedules = await AllSchedulesAsync(ct).ConfigureAwait(false);
        var latest = await db.AttendanceUploads.Where(u => u.CommittedAt != null).OrderByDescending(u => u.CommittedAt)
            .Select(u => u.CommittedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var mine = (await data.CoverageAsync([employee.Id], ct).ConfigureAwait(false)).GetValueOrDefault(employee.Id);
        return new CurrentStatusOut(now, today, settings.OfficeTimezone,
                                    ScheduleBrief.Of(AttendanceRules.ResolveSchedule(schedules, AttendanceData.EmployeeDataOf(employee), today)),
                                    await Timesheets.LatestImportThroughAsync(db, ct).ConfigureAwait(false),
                                    mine is { Items.Count: > 0 } ? mine.Items[^1].End : null, latest);
    }

    /// <summary>The work schedule that applies to the employee on a day (null when nothing applies).</summary>
    public async Task<ScheduleData?> ShiftForAsync(User employee, DateOnly day, CancellationToken ct = default) =>
        AttendanceRules.ResolveSchedule(await AllSchedulesAsync(ct).ConfigureAwait(false), AttendanceData.EmployeeDataOf(employee), day);

    public Task<AttendanceDaily?> TimesheetRowAsync(int employeeId, DateOnly day, CancellationToken ct = default) =>
        db.AttendanceDaily.FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.WorkDate == day, ct);

    /// <summary>One day's record and the timesheet entry behind it (GET .../days/{day}).</summary>
    public async Task<DayDetailOut> DayDetailAsync(User employee, DateOnly day, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);
        var records = await RecordsForAsync(employee, day, day, ct).ConfigureAwait(false);
        var row = await TimesheetRowAsync(employee.Id, day, ct).ConfigureAwait(false);
        TimesheetEntryOut? entry = null;
        if (row is not null)
        {
            var upload = row.UploadId is { } uid ? await db.AttendanceUploads.FindAsync([uid], ct).ConfigureAwait(false) : null;
            var by = row.OverriddenById is { } bid ? await db.Users.FindAsync([bid], ct).ConfigureAwait(false) : null;
            entry = new TimesheetEntryOut(row.WorkMode, row.WfoSource, row.UploadWorkMode, row.UploadMinutes, row.BadgeMinutes, row.FirstIn,
                                          row.LastOut, upload?.Filename, row.OverrideWorkMode, row.OverrideNote, by?.FullName,
                                          row.OverriddenAt, [.. row.Conflicts]);
        }

        var shift = await ShiftForAsync(employee, day, ct).ConfigureAwait(false);
        var policy = await Policies.GetAsync(db, ct).ConfigureAwait(false);
        return new DayDetailOut(records.Count > 0 ? DayRecordOut.Of(records[0]) : null, entry, ScheduleBrief.Of(shift), policy.LateGraceMinutes);
    }

    /// <summary>
    /// WFO and attendance figures for each calendar month of start..end, up to the current month
    /// (<c>attendance_export.month_rows</c>).
    /// </summary>
    public async Task<List<MonthOut>> MonthRowsAsync(User employee, IReadOnlyList<DayRecord> records, DateOnly start, DateOnly end,
                                                     CancellationToken ct = default) =>
        [.. (await MonthFiguresAsync(employee, records, start, end, ct).ConfigureAwait(false)).Select(m => m.Month)];

    /// <summary>
    /// WFO and attendance figures for each calendar month of start..end, up to the current month (<c>attendance_export.month_rows</c>),
    /// with the days without a timesheet that the exports also show.
    /// </summary>
    public async Task<List<(MonthOut Month, int NoTimesheetDays)>> MonthFiguresAsync(User employee, IReadOnlyList<DayRecord> records, DateOnly start,
                                                                                    DateOnly end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var today = data.Today;
        var output = new List<(MonthOut, int)>();
        for (var first = new DateOnly(start.Year, start.Month, 1); first <= end; first = first.AddMonths(1))
        {
            var last = OfficeTime.MonthEnd(first);
            var (mStart, mEnd) = (first > start ? first : start, last < end ? last : end);
            if (mStart > today)
            {
                break;
            }

            var w = await wfo.RangeSummaryAsync(employee, mStart, mEnd, ct).ConfigureAwait(false);
            var s = AttendanceRules.Summarize(records.Where(r => mStart <= r.Date && r.Date <= mEnd), mStart, mEnd);
            output.Add((new MonthOut(first.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), mStart, mEnd,
                                     (mStart, mEnd) != (first, last), s.PresentDays + s.AbsentDays + s.WfhDays + s.LeaveDays > 0,
                                     w.WorkingDays, w.WfoDays, w.RequiredDays, w.WfoBand, w.WfhDays, w.LeaveDays, s.AbsentDays,
                                     s.AttendancePercentage, s.AverageNetMinutes), s.NoTimesheetDays));
        }

        return output;
    }
}
