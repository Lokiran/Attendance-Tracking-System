using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Ats.Application.Python;
using Ats.Domain;
using Ats.Domain.Rules;
using FluentAssertions;

namespace Ats.Tests.Rules;

/// <summary>
/// Replays fixtures produced by the Python backend's own rule functions (tools/make_rule_fixtures.py): the .NET
/// engines must give exactly the same day records, summaries, classifications, WFO figures and rounding.
/// </summary>
public sealed class RuleParityTests
{
    private static JsonElement Load(string name)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip).RootElement.Clone();
    }

    private static DateOnly D(JsonElement e) => DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? OptD(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : D(e);

    private static int? OptI(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();

    private static string? OptS(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

    private static WorkMode? Mode(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : EnumText.Parse<WorkMode>(e.GetString()!);

    private static DateTimeOffset? Dt(JsonElement e) =>
        e.ValueKind == JsonValueKind.Null ? null : DateTimeOffset.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    private static double? OptDouble(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDouble();

    [Fact]
    public void Attendance_records_and_summaries_match_python()
    {
        var problems = new List<string>();
        var n = 0;
        foreach (var c in Load("rules-attendance").EnumerateArray())
        {
            n++;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(c.GetProperty("zone").GetString()!);
            var emp = c.GetProperty("employee");
            var employee = new EmployeeData(emp.GetProperty("id").GetInt32(), OptI(emp.GetProperty("department_id")), D(emp.GetProperty("date_joined")));
            var schedules = c.GetProperty("schedules").EnumerateArray().Select(s => new ScheduleData(
                s.GetProperty("id").GetInt32(), OptI(s.GetProperty("employee_id")), OptI(s.GetProperty("department_id")),
                TimeOnly.Parse(s.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture),
                TimeOnly.Parse(s.GetProperty("end_time").GetString()!, CultureInfo.InvariantCulture), s.GetProperty("required_minutes").GetInt32(),
                s.GetProperty("working_days").EnumerateArray().Select(x => x.GetInt32()).ToHashSet(), D(s.GetProperty("effective_from")),
                OptD(s.GetProperty("effective_to")), s.GetProperty("name").GetString()!)).ToList();
            var holidays = c.GetProperty("holidays").EnumerateArray()
                .Select(h => new HolidayData(D(h.GetProperty("date")), h.GetProperty("name").GetString()!, OptI(h.GetProperty("department_id")))).ToList();
            var timesheet = c.GetProperty("timesheet").EnumerateArray().Select(t => new TimesheetDay(
                t.GetProperty("employee_id").GetInt32(), D(t.GetProperty("date")), Mode(t.GetProperty("work_mode")), OptI(t.GetProperty("minutes")),
                Dt(t.GetProperty("first_in")), Dt(t.GetProperty("last_out")))).ToList();
            Func<DateOnly, bool>? covered = null;
            if (c.GetProperty("covered").ValueKind != JsonValueKind.Null)
            {
                var periods = c.GetProperty("covered").EnumerateArray().Select(p => (D(p[0]), D(p[1]))).ToList();
                covered = d => periods.Any(p => p.Item1 <= d && d <= p.Item2);
            }

            var pol = c.GetProperty("policy");
            var policy = new PolicyData(pol.GetProperty("min_present_minutes").GetInt32(), pol.GetProperty("late_grace_minutes").GetInt32());
            var (start, end) = (D(c.GetProperty("start")), D(c.GetProperty("end")));
            var records = AttendanceRules.BuildDayRecords(employee, start, end, D(c.GetProperty("today")), zone, timesheet, schedules,
                                                          holidays, policy, covered);
            var expected = c.GetProperty("records").EnumerateArray().ToList();
            if (records.Count != expected.Count)
            {
                problems.Add($"case {n}: {records.Count} records vs {expected.Count}");
                continue;
            }

            for (var i = 0; i < records.Count; i++)
            {
                var (r, x) = (records[i], expected[i]);
                var mine = $"{r.Date:yyyy-MM-dd}|{EnumText.Of(r.Status)}|{r.Expected}|{r.ScheduledWorkday}|{r.RequiredMinutes}|{r.NetMinutes}|{r.HoursKnown}|" +
                           $"{(r.WorkMode is { } m ? EnumText.Of(m) : "")}|{(r.FirstIn is { } fi ? PyFormat.IsoDateTime(fi) : "")}|" +
                           $"{(r.LastOut is { } lo ? PyFormat.IsoDateTime(lo) : "")}|{r.HolidayName}|{string.Join(",", r.Exceptions)}";
                var theirs = $"{x.GetProperty("date").GetString()}|{x.GetProperty("status").GetString()}|{x.GetProperty("expected").GetBoolean()}|" +
                             $"{x.GetProperty("scheduled_workday").GetBoolean()}|{x.GetProperty("required_minutes").GetInt32()}|{x.GetProperty("net_minutes").GetInt32()}|" +
                             $"{x.GetProperty("hours_known").GetBoolean()}|{OptS(x.GetProperty("work_mode"))}|{OptS(x.GetProperty("first_in"))}|" +
                             $"{OptS(x.GetProperty("last_out"))}|{OptS(x.GetProperty("holiday_name"))}|{string.Join(",", x.GetProperty("exceptions").EnumerateArray().Select(e => e.GetString()))}";
                if (mine != theirs)
                {
                    problems.Add($"case {n} ({c.GetProperty("zone").GetString()}): {mine}  vs  {theirs}");
                }
            }

            var s = AttendanceRules.Summarize(records, start, end);
            var xs = c.GetProperty("summary");
            var got = $"{s.ExpectedDays}|{s.PresentDays}|{s.PresentExpectedDays}|{s.ExtraPresentDays}|{s.AbsentDays}|{s.WfhDays}|{s.LeaveDays}|{s.HolidayDays}|" +
                      $"{s.WeekendDays}|{s.NotScheduledDays}|{s.NoTimesheetDays}|{s.NetMinutes}|{s.RequiredMinutes}|{s.HoursDays}|{s.HoursRequiredMinutes}|" +
                      $"{s.LateDays}|{s.VarianceMinutes}|{s.AverageNetMinutes}|{s.AttendancePercentage?.ToString("R", CultureInfo.InvariantCulture)}|{s.CountedDays}";
            var want = string.Join("|", new[]
            {
                "expected_days", "present_days", "present_expected_days", "extra_present_days", "absent_days", "wfh_days", "leave_days",
                "holiday_days", "weekend_days", "not_scheduled_days", "no_timesheet_days", "net_minutes", "required_minutes", "hours_days",
                "hours_required_minutes", "late_days", "variance_minutes", "average_net_minutes", "attendance_percentage", "counted_days",
            }.Select(k => xs.GetProperty(k) switch
            {
                { ValueKind: JsonValueKind.Null } => "",
                var v when k == "attendance_percentage" => v.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                var v => v.GetInt32().ToString(CultureInfo.InvariantCulture),
            }));
            if (got != want)
            {
                problems.Add($"case {n} summary: {got}  vs  {want}");
            }
        }

        n.Should().Be(300);
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Day_classification_matches_python()
    {
        var problems = new List<string>();
        foreach (var c in Load("rules-evidence").EnumerateArray())
        {
            var e = c.GetProperty("evidence");
            var evidence = new DayEvidence(
                e.GetProperty("scheduled_workday").GetBoolean(), e.GetProperty("holiday").GetBoolean(), Mode(e.GetProperty("override")),
                Mode(e.GetProperty("upload_mode")), OptI(e.GetProperty("upload_minutes")), OptI(e.GetProperty("badge_minutes")),
                OptI(e.GetProperty("wifi_minutes")), e.GetProperty("checkin_office_minutes").GetInt32(), e.GetProperty("checkin_wfh_minutes").GetInt32(),
                e.GetProperty("checkin_untagged_minutes").GetInt32(), e.GetProperty("checkin_office_seen").GetBoolean(), e.GetProperty("checkin_wfh_seen").GetBoolean());
            var cls = WfoRules.ClassifyDay(evidence);
            var status = WfoRules.DayStatusOf(cls, c.GetProperty("full_day_minutes").GetInt32(), c.GetProperty("half_day_minutes").GetInt32());
            var x = c.GetProperty("class");
            var mine = $"{(cls.WorkMode is { } m ? EnumText.Of(m) : "")}|{(cls.Source is { } src ? EnumText.Of(src) : "")}|{cls.OfficeMinutes}|{cls.WfhMinutes}|" +
                       $"{string.Join(";", cls.Conflicts)}|{cls.HoursKnown}|{EnumText.Of(status)}";
            var theirs = $"{OptS(x.GetProperty("work_mode"))}|{OptS(x.GetProperty("source"))}|{x.GetProperty("office_minutes").GetInt32()}|" +
                         $"{x.GetProperty("wfh_minutes").GetInt32()}|{string.Join(";", x.GetProperty("conflicts").EnumerateArray().Select(v => v.GetString()))}|" +
                         $"{x.GetProperty("hours_known").GetBoolean()}|{x.GetProperty("day_status").GetString()}";
            if (mine != theirs)
            {
                problems.Add($"{e.GetRawText()}: {mine}  vs  {theirs}");
            }
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void Wfo_policy_figures_match_python()
    {
        var problems = new List<string>();
        var n = 0;
        foreach (var c in Load("rules-wfo").EnumerateArray())
        {
            n++;
            var weekdays = c.GetProperty("weekdays").EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
            var joined = D(c.GetProperty("joined"));
            var days = c.GetProperty("days").EnumerateArray().Select(p => new PolicyDay(
                D(p.GetProperty("date")), Mode(p.GetProperty("work_mode")), p.GetProperty("office_minutes").GetInt32(),
                p.GetProperty("scheduled").GetBoolean(), p.GetProperty("hours_known").GetBoolean())).ToList();
            var pol = c.GetProperty("policy");
            var policy = new WfoPolicy(pol.GetProperty("min_days_per_week").GetInt32(), pol.GetProperty("min_days_per_month").GetInt32(),
                                       pol.GetProperty("min_office_minutes").GetInt32());
            var s = WfoRules.SummarizeWfo(days, D(c.GetProperty("start")), D(c.GetProperty("end")), D(c.GetProperty("today")), policy,
                                          d => d >= joined && weekdays.Contains(OfficeTime.IsoWeekday(d)));
            var x = c.GetProperty("summary");
            var mine = $"{s.WorkingDays}|{s.StandardDays}|{s.WfoDays}|{s.WfhDays}|{s.OfficeDaysBelowMinimum}|{s.LeaveDays}|{s.HolidayDays}|{s.WeekoffDays}|" +
                       $"{s.RequiredDays}|{s.ShortfallDays}|{s.WfoPercentage?.ToString("R", CultureInfo.InvariantCulture)}|{s.Compliance}";
            var theirs = $"{x.GetProperty("working_days")}|{x.GetProperty("standard_days")}|{x.GetProperty("wfo_days")}|{x.GetProperty("wfh_days")}|" +
                         $"{x.GetProperty("office_days_below_minimum")}|{x.GetProperty("leave_days")}|{x.GetProperty("holiday_days")}|{x.GetProperty("weekoff_days")}|" +
                         $"{x.GetProperty("required_days")}|{x.GetProperty("shortfall_days")}|{OptDouble(x.GetProperty("wfo_percentage"))?.ToString("R", CultureInfo.InvariantCulture)}|" +
                         $"{x.GetProperty("compliance").GetString()}";
            if (mine != theirs)
            {
                problems.Add($"case {n} summary: {mine}  vs  {theirs}");
            }

            var weeks = c.GetProperty("weeks").EnumerateArray().Select(w =>
                $"{w.GetProperty("week_start").GetString()}|{w.GetProperty("week_end").GetString()}|{w.GetProperty("label").GetString()}|" +
                $"{w.GetProperty("working_days")}|{w.GetProperty("wfo_days")}|{w.GetProperty("required")}|{w.GetProperty("status").GetString()}|{w.GetProperty("shortfall")}").ToList();
            var myWeeks = s.Weeks.Select(w => $"{w.WeekStart:yyyy-MM-dd}|{w.WeekEnd:yyyy-MM-dd}|{w.Label}|{w.WorkingDays}|{w.WfoDays}|{w.Required}|{w.Status}|{w.Shortfall}").ToList();
            if (!weeks.SequenceEqual(myWeeks))
            {
                problems.Add($"case {n} weeks: {string.Join(" / ", myWeeks)}  vs  {string.Join(" / ", weeks)}");
            }
        }

        n.Should().Be(300);
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Rounding_matches_python()
    {
        var problems = new List<string>();
        foreach (var c in Load("rules-rounding").EnumerateArray())
        {
            var (present, counted) = (c.GetProperty("present").GetInt32(), c.GetProperty("counted").GetInt32());
            var x = c.GetProperty("x").GetDouble();
            var y = c.GetProperty("y").GetDouble();
            if (present != 0 || counted != 1)
            {
                if (AttendanceRules.AttendanceRate(present, counted) != c.GetProperty("rate").GetDouble())
                {
                    problems.Add($"rate {present}/{counted}");
                }

                if (PyMath.RoundToInt((double)present / counted) != c.GetProperty("round0").GetInt32())
                {
                    problems.Add($"round({present}/{counted})");
                }
            }
            else if (PyMath.RoundToInt(x) != c.GetProperty("round0").GetInt32())
            {
                problems.Add($"round({x.ToString("R", CultureInfo.InvariantCulture)})");
            }

            if (PyMath.Round(x, 1) != c.GetProperty("round1").GetDouble())
            {
                problems.Add($"round({x.ToString("R", CultureInfo.InvariantCulture)}, 1): {PyMath.Round(x, 1)} vs {c.GetProperty("round1").GetDouble()}");
            }

            if (PyMath.RoundHalfUp(y) != c.GetProperty("half_up").GetInt32())
            {
                problems.Add($"round_half_up({y.ToString("R", CultureInfo.InvariantCulture)})");
            }
        }

        problems.Should().BeEmpty();
    }
}
