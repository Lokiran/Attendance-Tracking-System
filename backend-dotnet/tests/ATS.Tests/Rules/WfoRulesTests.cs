using Ats.Application.Uploads;
using Ats.Domain;
using Ats.Domain.Rules;
using Ats.Tests.Uploads;
using FluentAssertions;

namespace Ats.Tests.Rules;

/// <summary>WFO classification priority, conflicts, text mapping and policy compliance (port of backend/app/tests/test_wfo_rules.py).</summary>
public sealed class WfoRulesTests
{
    private const WorkMode O = WorkMode.Office;
    private const WorkMode H = WorkMode.Wfh;
    private static WfoMapping Mapping => UploadParityTests.Mapping.Value;

    // ------------------------------------------------------------------ text mapping (config file)

    [Theory]
    [InlineData("WFO", O)]
    [InlineData("Office", O)]
    [InlineData("On-site", O)]
    [InlineData("ON_SITE", O)]
    [InlineData("  on   site ", O)]
    [InlineData("onsite", O)]
    [InlineData("WFH", H)]
    [InlineData("Remote", H)]
    [InlineData("work from home", H)]
    [InlineData("Leave", WorkMode.Leave)]
    [InlineData("Week Off", WorkMode.Weekoff)]
    [InlineData("holiday", WorkMode.Holiday)]
    [InlineData("OFFICE", O)]
    public void Work_mode_text_mapping_from_config(string text, WorkMode mode) => Mapping.WorkMode(text).Should().Be(mode);

    [Fact]
    public void Unknown_text_and_badge_sources()
    {
        Mapping.WorkMode("hybrid-ish").Should().BeNull();
        Mapping.WorkMode("").Should().BeNull();
        (Mapping.IsBadge("Badge"), Mapping.IsBadge("card-swipe"), Mapping.IsBadge("HRMS")).Should().Be((true, true, false));
        (Mapping.Column("Employee Code"), Mapping.Column("Work Location")).Should().Be(("employee", "work_mode"));
        Mapping.Column("Status").Should().BeNull(); // deliberately not a work-mode column (often Present / Absent)
    }

    [Fact]
    public void Mapping_file_is_validated()
    {
        FluentActions.Invoking(() => WfoMapping.Parse("""{"work_modes": {"HYBRID": ["hybrid"]}}""")).Should().Throw<InvalidOperationException>().WithMessage("*unknown work mode*");
        FluentActions.Invoking(() => WfoMapping.Parse("""{"work_modes": {"OFFICE": ["x"], "WFH": ["x"]}}""")).Should().Throw<InvalidOperationException>().WithMessage("*both*");
        FluentActions.Invoking(() => WfoMapping.Parse("""{"columns": {"salary": ["pay"]}}""")).Should().Throw<InvalidOperationException>().WithMessage("*unknown column*");
    }

    // ------------------------------------------------------------------ classification priority

    private static DayClass C(DayEvidence e) => WfoRules.ClassifyDay(e);

    [Fact]
    public void Priority_1_upload_column_beats_lower_sources()
    {
        var c = C(new DayEvidence(UploadMode: O, UploadMinutes: 480, WifiMinutes: 300, CheckinOfficeSeen: true));
        (c.WorkMode, c.Source, c.OfficeMinutes).Should().Be((O, WfoSource.UploadColumn, 480));
    }

    [Fact]
    public void Priority_2_badge_then_3_wifi_then_4_checkin()
    {
        C(new DayEvidence(BadgeMinutes: 400, WifiMinutes: 500)).Source.Should().Be(WfoSource.Badge);
        C(new DayEvidence(WifiMinutes: 500, CheckinOfficeMinutes: 450)).Source.Should().Be(WfoSource.Wifi);
        C(new DayEvidence(CheckinOfficeMinutes: 450)).Source.Should().Be(WfoSource.CheckIn);
        C(new DayEvidence(BadgeMinutes: 400, WifiMinutes: 500)).OfficeMinutes.Should().Be(500); // the longest office evidence, never the sum
    }

    [Fact]
    public void Priority_5_hours_without_office_signal_are_wfh()
    {
        var c = C(new DayEvidence(CheckinUntaggedMinutes: 420));
        (c.WorkMode, c.Source, c.WfhMinutes).Should().Be((H, WfoSource.Inferred, 420));
        C(new DayEvidence(UploadMinutes: 360)).WorkMode.Should().Be(H); // hours in file, no mode, no badge
    }

    [Fact]
    public void Upload_wfh_and_checkin_wfh()
    {
        C(new DayEvidence(UploadMode: H, UploadMinutes: 480)).Source.Should().Be(WfoSource.UploadColumn);
        var c = C(new DayEvidence(CheckinWfhMinutes: 470, CheckinWfhSeen: true));
        (c.WorkMode, c.Source, c.WfhMinutes).Should().Be((H, WfoSource.CheckIn, 470));
    }

    [Fact]
    public void Conflict_office_and_wfh_marks_office_and_logs_it()
    {
        var c = C(new DayEvidence(UploadMode: H, UploadMinutes: 480, BadgeMinutes: 420));
        (c.WorkMode, c.Source, c.OfficeMinutes).Should().Be((O, WfoSource.Badge, 420));
        c.Conflicts.Should().Equal("UPLOAD_COLUMN said WFH; office signal from BADGE");
        c = C(new DayEvidence(WifiMinutes: 300, CheckinWfhSeen: true, CheckinWfhMinutes: 60));
        c.WorkMode.Should().Be(O);
        c.Conflicts.Should().Equal("CHECK_IN said WFH; office signal from WIFI");
    }

    [Fact]
    public void Office_signal_on_leave_day_wins_with_conflict()
    {
        var c = C(new DayEvidence(UploadMode: WorkMode.Leave, BadgeMinutes: 300));
        c.WorkMode.Should().Be(O);
        c.Conflicts[0].Should().Contain("UPLOAD_COLUMN said LEAVE");
    }

    [Fact]
    public void Leave_holiday_weekoff_from_file_and_calendar()
    {
        C(new DayEvidence(UploadMode: WorkMode.Leave)).WorkMode.Should().Be(WorkMode.Leave);
        C(new DayEvidence(Holiday: true)).WorkMode.Should().Be(WorkMode.Holiday);
        var c = C(new DayEvidence(ScheduledWorkday: false));
        (c.WorkMode, c.Source).Should().Be((WorkMode.Weekoff, WfoSource.Calendar));
        C(new DayEvidence()).WorkMode.Should().BeNull(); // scheduled day, nothing at all: absent
        c = C(new DayEvidence(UploadMode: WorkMode.Leave, CheckinUntaggedMinutes: 120));
        c.WorkMode.Should().Be(WorkMode.Leave);
        c.Conflicts.Should().NotBeEmpty(); // leave in file but hours checked in
    }

    [Fact]
    public void Working_on_a_holiday_or_weekend_is_still_classified()
    {
        C(new DayEvidence(Holiday: true, CheckinOfficeMinutes: 300)).WorkMode.Should().Be(O);
        C(new DayEvidence(ScheduledWorkday: false, CheckinUntaggedMinutes: 200)).WorkMode.Should().Be(H);
    }

    [Fact]
    public void Override_always_wins()
    {
        var c = C(new DayEvidence(Override: H, BadgeMinutes: 480, UploadMode: O));
        (c.WorkMode, c.Source, c.Conflicts.Count).Should().Be((H, WfoSource.ManualOverride, 0));
        C(new DayEvidence(Override: O, WifiMinutes: 300)).OfficeMinutes.Should().Be(300);
        c = C(new DayEvidence(Override: O, CheckinUntaggedMinutes: 480)); // overriding to OFFICE claims untagged hours
        (c.OfficeMinutes, c.WfhMinutes).Should().Be((480, 0));
        c = C(new DayEvidence(Override: WorkMode.Leave, CheckinUntaggedMinutes: 480));
        (c.OfficeMinutes, c.WfhMinutes).Should().Be((0, 0));
    }

    [Fact]
    public void Day_status_from_minutes()
    {
        DailyStatus S(DayEvidence e) => WfoRules.DayStatusOf(C(e), 480, 240);
        S(new DayEvidence(BadgeMinutes: 500)).Should().Be(DailyStatus.Present);
        S(new DayEvidence(BadgeMinutes: 300)).Should().Be(DailyStatus.HalfDay);
        S(new DayEvidence(BadgeMinutes: 0)).Should().Be(DailyStatus.Present); // a swipe with no time span: present, hours unknown
        S(new DayEvidence(BadgeMinutes: 60)).Should().Be(DailyStatus.Absent);
        S(new DayEvidence(UploadMode: WorkMode.Leave)).Should().Be(DailyStatus.Leave);
        S(new DayEvidence(ScheduledWorkday: false)).Should().Be(DailyStatus.Weekend);
    }

    // ------------------------------------------------------------------ policy

    private static readonly WfoPolicy Policy = new(3, 12, 240);
    private static readonly DateOnly NovStart = new(2026, 11, 1), NovEnd = new(2026, 11, 30); // 1 Nov 2026 is a Sunday

    private static bool IsWeekday(DateOnly d) => OfficeTime.Weekday(d) < 5;

    private static List<PolicyDay> Month(Dictionary<DateOnly, (WorkMode? Mode, int Minutes)> modes, HashSet<DateOnly>? holidays = null,
                                         DateOnly? start = null, DateOnly? end = null)
    {
        holidays ??= [];
        var output = new List<PolicyDay>();
        for (var d = start ?? NovStart; d <= (end ?? NovEnd); d = d.AddDays(1))
        {
            var scheduled = IsWeekday(d) && !holidays.Contains(d);
            var (mode, minutes) = modes.TryGetValue(d, out var v) ? v : (holidays.Contains(d) ? WorkMode.Holiday : IsWeekday(d) ? null : WorkMode.Weekoff, 0);
            output.Add(new PolicyDay(d, mode, minutes, scheduled));
        }

        return output;
    }

    private static WfoSummary Summary(IEnumerable<PolicyDay> days, DateOnly? today = null, DateOnly? start = null, DateOnly? end = null) =>
        WfoRules.SummarizeWfo(days, start ?? NovStart, end ?? NovEnd, today ?? new DateOnly(2026, 12, 15), Policy, IsWeekday);

    private static Dictionary<DateOnly, (WorkMode?, int)> Office(IEnumerable<DateOnly> days, int minutes = 480) => days.ToDictionary(d => d, _ => ((WorkMode?)O, minutes));

    private static Dictionary<DateOnly, (WorkMode?, int)> Wfh(IEnumerable<DateOnly> days) => days.ToDictionary(d => d, _ => ((WorkMode?)H, 480));

    private static Dictionary<DateOnly, (WorkMode?, int)> Merge(params Dictionary<DateOnly, (WorkMode?, int)>[] parts) =>
        parts.SelectMany(p => p).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);

    private static List<DateOnly> WeekdaysOf(DateOnly start, DateOnly end)
    {
        var output = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (IsWeekday(d))
            {
                output.Add(d);
            }
        }

        return output;
    }

    private static DateOnly Nov(int day) => new(2026, 11, day);

    [Fact]
    public void Minimum_hours_rule()
    {
        var s = Summary(Month(Merge(Office([Nov(2)]), Office([Nov(3)], 239), Office([Nov(4)], 240))));
        (s.WfoDays, s.OfficeDaysBelowMinimum).Should().Be((2, 1)); // 239 min is OFFICE but not a WFO day
    }

    [Fact]
    public void Fully_compliant_month_three_days_a_week()
    {
        var wd = WeekdaysOf(NovStart, NovEnd);
        var officeDays = wd.Where(d => OfficeTime.Weekday(d) <= 2).ToList();
        var s = Summary(Month(Merge(Office(officeDays), Wfh(wd.Except(officeDays)))));
        (s.WorkingDays, s.StandardDays, s.WfoDays, s.WfhDays).Should().Be((21, 21, 13, 8));
        (s.RequiredDays, s.ShortfallDays, s.Compliance).Should().Be((12, 0, "COMPLIANT"));
        s.WfoPercentage.Should().Be(PyMath.Round(13 * 100.0 / 21, 1));
        s.Weeks.Should().OnlyContain(w => w.Status == "MET");
    }

    [Fact]
    public void Short_month_is_non_compliant_with_shortfall()
    {
        var wd = WeekdaysOf(NovStart, NovEnd);
        var s = Summary(Month(Merge(Office(wd.Take(8)), Wfh(wd.Skip(8)))));
        (s.WfoDays, s.RequiredDays, s.Compliance).Should().Be((8, 12, "NON_COMPLIANT"));
        s.ShortfallDays.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Leave_week_is_excluded_and_lowers_the_requirement()
    {
        var wd = WeekdaysOf(NovStart, NovEnd);
        var leave = wd.Where(d => d >= Nov(9) && d <= Nov(13)).ToList();
        var rest = wd.Except(leave).ToList();
        var officeDays = rest.Where(d => OfficeTime.Weekday(d) <= 2).ToList();
        var s = Summary(Month(Merge(leave.ToDictionary(d => d, _ => ((WorkMode?)WorkMode.Leave, 0)), Office(officeDays), Wfh(rest.Except(officeDays)))));
        (s.WorkingDays, s.LeaveDays).Should().Be((16, 5));
        s.RequiredDays.Should().Be(PyMath.RoundHalfUp(12 * 16 / 21.0)).And.Be(9);
        var leaveWeek = s.Weeks.First(w => w.WeekStart == Nov(9));
        (leaveWeek.WorkingDays, leaveWeek.Required, leaveWeek.Status).Should().Be((0, 0, "MET"));
        s.Compliance.Should().Be("COMPLIANT");
    }

    [Fact]
    public void Holiday_week_and_partial_weeks_are_prorated()
    {
        var holiday = Nov(4);
        var s = Summary(Month(Office(WeekdaysOf(NovStart, NovEnd).Where(d => d != holiday)), [holiday]));
        (s.StandardDays, s.HolidayDays).Should().Be((20, 1));
        var week = s.Weeks.First(w => w.WeekStart == Nov(2));
        (week.WorkingDays, week.Required).Should().Be((4, PyMath.RoundHalfUp(3 * 4 / 5.0)));
        var last = s.Weeks[^1];
        (last.WeekStart, last.WorkingDays, last.Required).Should().Be((Nov(30), 1, 1));
        (s.Weeks[0].WorkingDays, s.Weeks[0].Required).Should().Be((0, 0));
    }

    [Fact]
    public void One_short_week_makes_the_month_non_compliant_even_if_total_is_met()
    {
        var wd = WeekdaysOf(NovStart, NovEnd);
        var week2 = wd.Where(d => d >= Nov(9) && d <= Nov(13)).ToList();
        var s = Summary(Month(Merge(Office(wd.Except(week2)), Wfh(week2))));
        s.WfoDays.Should().Be(16).And.BeGreaterThanOrEqualTo(s.RequiredDays);
        (s.Compliance, s.ShortfallDays).Should().Be(("NON_COMPLIANT", 3));
    }

    [Fact]
    public void Office_day_on_a_weekend_counts_and_widens_the_denominator()
    {
        var s = Summary(Month(Merge(Office(WeekdaysOf(NovStart, NovEnd)), Office([Nov(7)]))));
        (s.WorkingDays, s.WfoDays, s.WfoPercentage).Should().Be((22, 22, (double?)100.0));
    }

    [Fact]
    public void Current_month_counts_completed_days_only()
    {
        var today = Nov(11);
        var wd = WeekdaysOf(NovStart, NovEnd);
        var s = Summary(Month(Merge(Office([Nov(2), Nov(3), Nov(4), Nov(9), Nov(11)]), Wfh(wd.Where(d => d < today && OfficeTime.Weekday(d) > 2)))), today);
        (s.WorkingDays, s.WfoDays).Should().Be((7, 4)); // today (the 11th) is not counted yet
        var current = s.Weeks.First(w => w.WeekStart == Nov(9));
        (current.Status, current.Shortfall).Should().Be(("IN_PROGRESS", 0));
        s.Weeks.Where(w => w.WeekStart > today).Select(w => w.Status).Should().Equal("UPCOMING", "UPCOMING", "UPCOMING");
        (s.RequiredDays, s.Compliance).Should().Be((PyMath.RoundHalfUp(12 * 7 / 21.0), "COMPLIANT"));
        s.RequiredDays.Should().Be(4);
    }

    [Fact]
    public void No_working_days_is_compliant_with_no_percentage()
    {
        var s = Summary(Month(WeekdaysOf(NovStart, NovEnd).ToDictionary(d => d, _ => ((WorkMode?)WorkMode.Leave, 0))));
        (s.WorkingDays, s.WfoPercentage, s.Compliance).Should().Be((0, (double?)null, "COMPLIANT"));
    }

    [Fact]
    public void Working_on_a_holiday_never_raises_the_requirement()
    {
        var holiday = Nov(4);
        var days = Month(Merge(Office(WeekdaysOf(NovStart, NovEnd).Where(d => d != holiday)), Wfh([holiday])), [holiday]);
        var i = days.FindIndex(d => d.Date == holiday);
        days[i] = new PolicyDay(holiday, H, 0, false); // the holiday was worked from home
        var s = Summary(days);
        (s.StandardDays, s.WorkingDays, s.RequiredDays).Should().Be((20, 21, 12));
        var week = s.Weeks.First(w => w.WeekStart == Nov(2));
        (week.WorkingDays, week.Required).Should().Be((5, 2));
    }

    [Fact]
    public void A_week_straddling_two_months_is_judged_on_all_its_days()
    {
        var days = new List<PolicyDay>();
        for (var d = new DateOnly(2026, 9, 25); d <= new DateOnly(2026, 11, 6); d = d.AddDays(1))
        {
            var wd = IsWeekday(d);
            WorkMode mode = wd ? (OfficeTime.Weekday(d) <= 2 ? O : H) : WorkMode.Weekoff;
            days.Add(new PolicyDay(d, mode, mode is O or H ? 480 : 0, wd));
        }

        var s = Summary(days, new DateOnly(2026, 12, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var first = s.Weeks[0];
        (first.WeekStart, first.WfoDays, first.Required, first.Status).Should().Be((new DateOnly(2026, 9, 28), 3, 3, "MET"));
        (s.WfoDays, s.Compliance).Should().Be((12, "COMPLIANT")); // monthly figures still count October only
    }

    [Fact]
    public void Office_days_without_any_hours_count_as_wfo_days()
    {
        var c = C(new DayEvidence(UploadMode: O));
        (c.WorkMode, c.HoursKnown).Should().Be((O, false));
        C(new DayEvidence(UploadMode: O, UploadMinutes: 120)).HoursKnown.Should().BeTrue();
        var days = Month(new() { [Nov(2)] = (O, 0), [Nov(3)] = (O, 120) }).Select(d => d with { HoursKnown = d.Date != Nov(2) });
        var s = Summary(days);
        (s.WfoDays, s.OfficeDaysBelowMinimum).Should().Be((1, 1)); // 2 Nov (unknown) counts, 3 Nov (2 h) does not
    }

    [Fact]
    public void Wfo_band_green_orange_red()
    {
        new[] { 14, 12, 11, 9, 8, 0 }.Select(d => WfoRules.WfoBand(d, 12)).Should().Equal("MET", "MET", "NEAR", "NEAR", "BELOW", "BELOW");
        (WfoRules.WfoBand(5, 5), WfoRules.WfoBand(0, 0)).Should().Be(("MET", "MET")); // prorated requirement
    }

    [Fact]
    public void A_period_of_several_months_adds_up_each_months_requirement()
    {
        var (octStart, novEnd) = (new DateOnly(2026, 10, 1), NovEnd);
        var s = Summary(Month(Office(WeekdaysOf(octStart, new DateOnly(2026, 10, 30))), start: octStart, end: novEnd), start: octStart, end: novEnd);
        (s.RequiredDays, s.WfoDays).Should().Be((24, 22)); // 12 for October + 12 for November
        WfoRules.WfoBand(s.WfoDays, s.RequiredDays).Should().Be("NEAR");
    }

    [Fact]
    public void Part_of_a_month_is_asked_for_its_share()
    {
        var (start, end) = (Nov(2), Nov(13)); // 10 of November's 21 working days
        var s = Summary(Month(Office(WeekdaysOf(start, end)), start: new DateOnly(2026, 10, 27), end: Nov(19)), start: start, end: end);
        (s.RequiredDays, s.WfoDays).Should().Be((6, 10));
    }
}
