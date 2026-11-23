using Ats.Application.Attendance;
using Ats.Domain;
using Ats.Domain.Rules;
using FluentAssertions;

namespace Ats.Tests.Rules;

/// <summary>The pure attendance rules engine (port of backend/app/tests/test_attendance_rules.py; no database).</summary>
public sealed class AttendanceRulesTests
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly EmployeeData Emp = new(1, 10, new DateOnly(2025, 1, 1));
    private static readonly ScheduleData Weekdays = new(1, null, null, new TimeOnly(9, 30), new TimeOnly(18, 30), 480, new HashSet<int> { 1, 2, 3, 4, 5 },
                                                        new DateOnly(2024, 1, 1), null);
    private static readonly PolicyData Policy = new();
    private static readonly DateOnly Mon = new(2026, 3, 9);
    private static readonly DateOnly Tue = Mon.AddDays(1), Wed = Mon.AddDays(2), Fri = Mon.AddDays(4), Sat = Mon.AddDays(5), Sun = Mon.AddDays(6);

    private static DateTimeOffset At(DateOnly d, int h, int m = 0) => OfficeTime.ToUtc(d, new TimeOnly(h, m), Ist);

    private static TimesheetDay Office(DateOnly d, int? minutes = null, DateTimeOffset? firstIn = null) => new(1, d, WorkMode.Office, minutes, firstIn);

    private static TimesheetDay Mode(DateOnly d, WorkMode m) => new(1, d, m);

    private static List<DayRecord> Records(DateOnly start, DateOnly end, IEnumerable<TimesheetDay>? timesheet = null, DateOnly? today = null,
                                           IReadOnlyList<ScheduleData>? schedules = null, IEnumerable<HolidayData>? holidays = null,
                                           PolicyData? policy = null, EmployeeData? employee = null, Func<DateOnly, bool>? covered = null) =>
        AttendanceRules.BuildDayRecords(employee ?? Emp, start, end, today ?? new DateOnly(2026, 3, 13), Ist, timesheet ?? [], schedules ?? [Weekdays],
                                        holidays ?? [], policy ?? Policy, covered);

    [Fact]
    public void Office_days_are_present_and_missing_covered_days_absent()
    {
        var recs = Records(Mon, Sun, [Office(Mon, 480), Office(Tue), Mode(Wed, WorkMode.Wfh)], today: new DateOnly(2026, 3, 16));
        recs.Select(r => r.Status).Should().Equal(DayStatus.Present, DayStatus.Present, DayStatus.Wfh, DayStatus.Absent, DayStatus.Absent,
                                                  DayStatus.Weekend, DayStatus.Weekend);
        recs[3].Exceptions.Should().Equal(Exceptions.Absent);
        var s = AttendanceRules.Summarize(recs, Mon, Sun);
        (s.ExpectedDays, s.PresentExpectedDays, s.WfhDays, s.AbsentDays).Should().Be((5, 2, 1, 2));
        s.AttendancePercentage.Should().Be(40.0); // office attendance: WFH counts as a working day, not present
    }

    [Fact]
    public void Days_without_a_covering_timesheet_are_not_absent()
    {
        var recs = Records(Mon, Fri, [Office(Mon)], covered: d => d <= Tue); // the sheet reached Tuesday only
        recs.Select(r => r.Status).Should().Equal(DayStatus.Present, DayStatus.Absent, DayStatus.NoTimesheet, DayStatus.NoTimesheet, DayStatus.NoTimesheet);
        var s = AttendanceRules.Summarize(recs, Mon, Fri);
        (s.ExpectedDays, s.NoTimesheetDays, s.AttendancePercentage).Should().Be((2, 3, 50.0));
    }

    [Fact]
    public void Today_without_data_is_never_absent()
    {
        var r = Records(Fri, Fri)[0];
        (r.Status, r.Expected).Should().Be((DayStatus.NoTimesheet, false));
    }

    [Fact]
    public void Leave_holiday_and_weekoff_rows_are_not_expected()
    {
        var recs = Records(Mon, Wed, [Mode(Mon, WorkMode.Leave), Mode(Tue, WorkMode.Holiday), Mode(Wed, WorkMode.Weekoff)]);
        recs.Select(r => r.Status).Should().Equal(DayStatus.Leave, DayStatus.Holiday, DayStatus.Weekend);
        recs.Should().OnlyContain(r => !r.Expected);
        var s = AttendanceRules.Summarize(recs, Mon, Wed);
        (s.LeaveDays, s.AttendancePercentage).Should().Be((1, (double?)null));
    }

    [Fact]
    public void Days_without_hours_are_present_without_short_hours()
    {
        var recs = Records(Mon, Tue, [Office(Mon), Office(Tue)]);
        recs.Should().OnlyContain(r => r.Status == DayStatus.Present && !r.HoursKnown && r.Exceptions.Count == 0);
        var s = AttendanceRules.Summarize(recs, Mon, Tue);
        (s.NetMinutes, s.HoursDays, s.AverageNetMinutes, s.VarianceMinutes).Should().Be((0, 0, (int?)null, 0));
    }

    [Fact]
    public void Recorded_hours_are_measured_against_the_shift_of_the_same_days()
    {
        var recs = Records(Mon, Wed, [Office(Mon, 480), Office(Tue, 420), Office(Wed)]);
        var s = AttendanceRules.Summarize(recs, Mon, Wed);
        (s.NetMinutes, s.HoursDays, s.HoursRequiredMinutes, s.VarianceMinutes, s.AverageNetMinutes, s.RequiredMinutes)
            .Should().Be((900, 2, 960, -60, (int?)450, 1440));
        recs[1].Exceptions.Should().Contain(Exceptions.ShortHours);
        recs[2].Exceptions.Should().NotContain(Exceptions.ShortHours);
    }

    [Fact]
    public void Late_arrival_from_timesheet_in_time()
    {
        var recs = Records(Mon, Tue, [Office(Mon, 480, At(Mon, 10)), Office(Tue, 480, At(Tue, 9, 40))]);
        recs[0].Exceptions.Should().Equal(Exceptions.LateArrival);
        recs[1].Exceptions.Should().BeEmpty();
        AttendanceRules.Summarize(recs, Mon, Tue).LateDays.Should().Be(1);
    }

    [Fact]
    public void Minimum_minutes_policy_applies_only_to_recorded_hours()
    {
        var recs = Records(Mon, Tue, [Office(Mon, 120), Office(Tue)], policy: new PolicyData(MinPresentMinutes: 240));
        (recs[0].Status, recs[0].Exceptions.Single()).Should().Be((DayStatus.Absent, Exceptions.BelowMinimum));
        recs[1].Status.Should().Be(DayStatus.Present);
    }

    [Fact]
    public void Holiday_excluded_and_department_specific_holiday()
    {
        var recs = Records(Mon, Wed, [Office(Mon)], holidays: [new HolidayData(Tue, "Founders Day", null), new HolidayData(Wed, "Team day", 99)]);
        recs.Select(r => r.Status).Should().Equal(DayStatus.Present, DayStatus.Holiday, DayStatus.Absent);
        recs[1].HolidayName.Should().Be("Founders Day");
    }

    [Fact]
    public void Work_on_an_off_day_is_an_extra_day_that_counts_as_attended()
    {
        var r = Records(Sat, Sat, [Office(Sat, 300)], today: new DateOnly(2026, 3, 16));
        (r[0].Status, r[0].Expected, r[0].RequiredMinutes).Should().Be((DayStatus.Present, false, 0));
        var s = AttendanceRules.Summarize(r, Sat, Sat);
        (s.ExtraPresentDays, s.ExpectedDays, s.CountedDays, s.AttendancePercentage).Should().Be((1, 0, 1, (double?)100.0));
    }

    [Fact]
    public void Office_day_on_a_holiday_counts_like_the_wfo_figures()
    {
        var recs = Records(Mon, Wed, [Office(Mon), Office(Tue)], holidays: [new HolidayData(Tue, "Optional holiday", null)]);
        recs.Select(r => r.Status).Should().Equal(DayStatus.Present, DayStatus.Present, DayStatus.Absent);
        (recs[1].Expected, recs[1].HolidayName).Should().Be((false, "Optional holiday"));
        var s = AttendanceRules.Summarize(recs, Mon, Wed);
        (s.PresentDays, s.ExpectedDays, s.ExtraPresentDays, s.CountedDays).Should().Be((2, 2, 1, 3));
        s.AttendancePercentage.Should().Be(PyMath.Round(2 * 100.0 / 3, 1));
        s.RequiredMinutes.Should().Be(2 * 480); // the holiday adds no shift hours
    }

    [Fact]
    public void Future_dates_are_not_evaluated() => Records(Fri, new DateOnly(2026, 3, 20)).Should().BeEquivalentTo(Records(Fri, Fri), o => o.WithStrictOrdering());

    [Fact]
    public void Days_before_joining_are_not_scheduled_even_with_data() =>
        Records(Mon, Wed, [Office(Mon)], employee: new EmployeeData(1, 10, Wed)).Select(r => r.Status)
            .Should().Equal(DayStatus.NotScheduled, DayStatus.NotScheduled, DayStatus.Absent);

    [Fact]
    public void Other_employees_rows_are_ignored() =>
        Records(Mon, Mon, [new TimesheetDay(2, Mon, WorkMode.Office)])[0].Status.Should().Be(DayStatus.Absent);

    private static readonly ScheduleData Dept = new(2, null, 10, new TimeOnly(8, 0), new TimeOnly(16, 0), 450, new HashSet<int> { 1, 2, 3, 4, 5, 6 },
                                                    new DateOnly(2024, 1, 1), null);

    [Fact]
    public void Schedule_resolution_priority_and_effective_dates()
    {
        var personal = new ScheduleData(3, 1, null, new TimeOnly(12, 0), new TimeOnly(20, 0), 420, new HashSet<int> { 1, 2, 3 }, new DateOnly(2026, 3, 11), null);
        var old = new ScheduleData(4, null, null, new TimeOnly(9, 0), new TimeOnly(17, 0), 400, new HashSet<int> { 1 }, new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31));
        ScheduleData[] schedules = [Weekdays, Dept, personal, old];
        AttendanceRules.ResolveSchedule(schedules, Emp, Tue)!.Id.Should().Be(2); // department beats org default
        AttendanceRules.ResolveSchedule(schedules, Emp, Wed)!.Id.Should().Be(3); // personal from its effective date
        AttendanceRules.ResolveSchedule([old], Emp, Tue).Should().BeNull();       // expired assignment
    }

    [Fact]
    public void Schedule_based_required_minutes_and_working_days()
    {
        var recs = Records(Sat, Sat, [Office(Sat, 450)], schedules: [Weekdays, Dept], today: new DateOnly(2026, 3, 16));
        (recs[0].Status, recs[0].Expected, recs[0].RequiredMinutes).Should().Be((DayStatus.Present, true, 450));
        AttendanceRules.Summarize(recs, Sat, Sat).VarianceMinutes.Should().Be(0);
    }

    [Fact]
    public void Shared_memo_gives_the_same_result()
    {
        var shared = new RulesMemo();
        List<DayRecord> Run() => AttendanceRules.BuildDayRecords(Emp, Mon, Fri, new DateOnly(2026, 3, 16), Ist, [Office(Mon)], [Weekdays], [], Policy, shared: shared);
        Run().Should().BeEquivalentTo(Run(), o => o.WithStrictOrdering()); // by value, as Python compares dataclasses
    }

    /// <summary>The fast path for plain timesheet rows must agree with the full classifier.</summary>
    [Fact]
    public void Timesheet_fast_path_matches_the_full_classifier()
    {
        var (tIn, tOut) = (At(Mon, 9, 30), At(Mon, 18));
        foreach (var mode in new WorkMode?[] { null, WorkMode.Office, WorkMode.Wfh, WorkMode.Leave, WorkMode.Holiday, WorkMode.Weekoff })
        {
            foreach (var minutes in new int?[] { null, 0, 300 })
            {
                var fast = AttendanceData.TimesheetDayOf(1, Mon, null, mode, minutes, null, null, tIn, tOut);
                var cls = WfoRules.ClassifyDay(new DayEvidence(UploadMode: mode, UploadMinutes: minutes));
                if (cls.WorkMode is null)
                {
                    fast.Should().BeNull($"{mode} {minutes}");
                    continue;
                }

                int? expected = cls.WorkMode == WorkMode.Office && cls.HoursKnown ? cls.OfficeMinutes : null;
                (fast!.WorkMode, fast.Minutes).Should().Be((cls.WorkMode, expected), $"{mode} {minutes}");
            }
        }

        var badge = AttendanceData.TimesheetDayOf(1, Mon, null, WorkMode.Wfh, null, 240, null, tIn, tOut); // badge: the full classifier
        (badge!.WorkMode, badge.Minutes).Should().Be((WorkMode.Office, 240));
    }
}
