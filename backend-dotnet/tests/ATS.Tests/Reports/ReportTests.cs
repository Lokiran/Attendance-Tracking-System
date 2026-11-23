using System.Globalization;
using System.Net;
using System.Text;
using Ats.Application.Reports;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Tests.Auth;
using ClosedXML.Excel;
using FluentAssertions;
using UglyToad.PdfPig;

namespace Ats.Tests.Reports;

/// <summary>
/// The exports (ports of backend/app/tests/test_attendance_export.py and the report tests of test_dashboards_reports.py).
/// Two months for Rahul: 12 office days in February (on target), 9 in March with 7.5 h each ("Close"); clock 15 April 2026.
/// </summary>
public sealed class ReportTests : IDisposable
{
    private const string Period = "start=2026-02-01&end=2026-03-31";
    private static readonly int[] FebDays = [2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 16, 17];
    private static readonly int[] MarDays = [2, 3, 4, 5, 6, 9, 10, 11, 12];
    private readonly AuthHost _host = new();

    public ReportTests() => Seed(_host);

    private static void Seed(AuthHost host)
    {
        host.Clock.UtcNow = new DateTimeOffset(2026, 4, 15, 6, 30, 0, TimeSpan.Zero);
        using var db = host.Db();
        var rahul = host.Org.Rahul.Id;
        foreach (var (month, days, minutes) in new[] { (2, FebDays, (int?)null), (3, MarDays, 450) })
        {
            foreach (var d in days)
            {
                db.AttendanceDaily.Add(new AttendanceDaily
                {
                    EmployeeId = rahul, WorkDate = new DateOnly(2026, month, d), OfficeMinutes = 0, WfhMinutes = 0, Corrected = false, Conflicts = [],
                    Status = DailyStatus.Absent, UploadWorkMode = WorkMode.Office, UploadMinutes = minutes,
                });
            }
        }

        // A committed timesheet covering February and March for Rahul (Python's factory.cover).
        var upload = new AttendanceUpload
        {
            Filename = "timesheet_2026-02-01.csv", FileSha256 = new string('0', 64), FileSize = 1, Status = UploadStatus.Committed, RowCount = 1,
            CommittedAt = new DateTimeOffset(2026, 3, 31, 23, 0, 0, TimeSpan.Zero), PeriodStart = new DateOnly(2026, 2, 1), PeriodEnd = new DateOnly(2026, 3, 31),
        };
        db.AttendanceUploads.Add(upload);
        db.SaveChanges();
        db.AttendanceUploadRows.Add(new AttendanceUploadRow
        {
            UploadId = upload.Id, RowNumber = 2, EmployeeRef = rahul.ToString(CultureInfo.InvariantCulture), EmployeeId = rahul,
            WorkDate = new DateOnly(2026, 2, 1),
        });
        db.SaveChanges();
    }

    public void Dispose() => _host.Dispose();

    private Auth.Org Org => _host.Org;

    private Task<HttpResponseMessage> Get(string path) => _host.Client.GetAsync(new Uri(path, UriKind.Relative));

    private static Dictionary<string, IXLRow> SummaryRows(XLWorkbook wb) =>
        wb.Worksheet("Summary").RowsUsed().Where(r => !r.Cell(1).IsEmpty()).GroupBy(r => r.Cell(1).GetString()).ToDictionary(g => g.Key, g => g.First());

    [Fact]
    public async Task Excel_export_has_summary_months_and_days()
    {
        await _host.LoginAsync(Org.Rahul);
        var r = await Get($"/api/reports/employee-attendance.xlsx?{Period}&employee_id={Org.Rahul.Id}");
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        r.Content.Headers.ContentType!.MediaType.Should().StartWith("application/vnd.openxmlformats");
        r.Content.Headers.ContentDisposition!.ToString().Should().Be($"attachment; filename=\"attendance_{Org.Rahul.EmployeeCode}_2026-02-01_2026-03-31.xlsx\"");
        r.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var wb = new XLWorkbook(await r.Content.ReadAsStreamAsync());
        wb.Worksheets.Select(w => w.Name).Should().Equal("Summary", "By month", "Daily");
        var summary = SummaryRows(wb);
        summary["Employee"].Cell(2).GetString().Should().Be("Rahul Reddy");
        summary["Period"].Cell(2).GetString().Should().Be("01 Feb 2026 – 31 Mar 2026");
        // Two months: the requirement is 12 for each, so 21 office days of 24 is 3 short.
        (summary["Office (WFO) days"].Cell(2).GetValue<int>(), summary["Office (WFO) days"].Cell(3).GetString())
            .Should().Be((21, "of 24 required · Close · on target in 1 of 2 months"));
        summary["Average office hours"].Cell(2).GetString().Should().Be("7h 30m");

        var months = wb.Worksheet("By month");
        Enumerable.Range(1, 5).Select(c => months.Cell(1, c).GetString()).Should().Equal("Month", "Working days", "Office days", "Required", "WFO status");
        (months.Cell(2, 1).GetString(), months.Cell(2, 2).GetValue<int>(), months.Cell(2, 3).GetValue<int>(), months.Cell(2, 4).GetValue<int>(), months.Cell(2, 5).GetString())
            .Should().Be(("Feb 2026", 20, 12, 12, "On target"));
        (months.Cell(3, 1).GetString(), months.Cell(3, 3).GetValue<int>(), months.Cell(3, 5).GetString()).Should().Be(("Mar 2026", 9, "Close"));
        months.Cell(2, 11).IsEmpty().Should().BeTrue();
        months.Cell(3, 11).GetValue<double>().Should().Be(7.5); // hours only where the timesheet has them

        var days = wb.Worksheet("Daily");
        days.LastRowUsed()!.RowNumber().Should().Be(1 + 59);
        var byDate = days.RowsUsed().Skip(1).ToDictionary(row => DateOnly.FromDateTime(row.Cell(1).GetDateTime()));
        byDate[new DateOnly(2026, 2, 2)].Cell(3).GetString().Should().Be("In office");
        byDate[new DateOnly(2026, 2, 18)].Cell(3).GetString().Should().Be("Absent");
        byDate[new DateOnly(2026, 2, 7)].Cell(3).GetString().Should().Be("Weekly off");
        byDate[new DateOnly(2026, 3, 2)].Cell(6).GetValue<double>().Should().Be(7.5);
        using var db = _host.Db();
        db.AuditLogs.Count(a => a.Action == "REPORT_EXPORTED").Should().Be(1);
        db.AuditLogs.Single(a => a.Action == "REPORT_EXPORTED").AfterValues!.ToJsonString().Should()
            .Be($$"""{"employee_id":{{Org.Rahul.Id}},"start":"2026-02-01","end":"2026-03-31","format":"xlsx"}""");
    }

    [Fact]
    public async Task Pdf_export_carries_the_brand_and_the_figures()
    {
        using var host = new AuthHost(new Dictionary<string, string?>
        {
            ["REPORT_COMPANY_NAME"] = "Acme Corp", ["REPORT_BRAND_COLOR"] = "#1F6F5A", ["REPORT_LOGO_FILE"] = "data/does-not-exist.png",
        });
        Seed(host);
        await host.LoginAsync(host.Org.Priya); // Rahul's manager
        var r = await host.Client.GetAsync(new Uri($"/api/reports/employee-attendance.pdf?{Period}&employee_id={host.Org.Rahul.Id}", UriKind.Relative));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
        r.Content.Headers.ContentDisposition!.ToString().Should().EndWith(".pdf\"");
        using var pdf = PdfDocument.Open(bytes);
        var text = string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
        foreach (var expected in new[] { "Acme Corp", "Attendance report", "Rahul Reddy", "01 Feb 2026 – 31 Mar 2026", "21 of 24", "Office days by month",
                                         "Feb 2026", "Mar 2026", "On target", "Close", "Daily attendance", "Page 1 of" })
        {
            text.Should().Contain(expected);
        }

        pdf.Information.Title.Should().Be("Attendance report – Rahul Reddy");
        pdf.Information.Author.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task Exports_follow_the_same_access_rule_as_the_dashboards()
    {
        await _host.LoginAsync(Org.Rahul);
        foreach (var ext in new[] { "xlsx", "pdf", "csv" })
        {
            (await Get($"/api/reports/employee-attendance.{ext}?{Period}&employee_id={Org.Rahul.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Get($"/api/reports/employee-attendance.{ext}?{Period}&employee_id={Org.Sneha.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        await _host.LoginAsync(Org.Anil); // a manager, but not of Rahul
        (await Get($"/api/reports/employee-attendance.pdf?{Period}&employee_id={Org.Rahul.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var path in new[] { "/api/reports/department-attendance.csv", "/api/org/wfo/export.xlsx" })
        {
            (await Get(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        await _host.LoginAsync(Org.Rahul);
        (await Get("/api/reports/team-attendance.csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Excel_export_neutralises_formulas_and_awkward_ids()
    {
        using (var db = _host.Db())
        {
            var rahul = db.Users.Single(u => u.Id == Org.Rahul.Id);
            rahul.FullName = "=HYPERLINK(\"http://evil\")";
            rahul.EmployeeCode = "Intern - 10003";
            db.SaveChanges();
        }

        await _host.LoginAsync(Org.Admin);
        var r = await Get($"/api/reports/employee-attendance.xlsx?{Period}&employee_id={Org.Rahul.Id}");
        r.Content.Headers.ContentDisposition!.ToString().Should().Contain("filename=\"attendance_Intern_-_10003_2026-02-01_2026-03-31.xlsx\"");
        using var wb = new XLWorkbook(await r.Content.ReadAsStreamAsync());
        SummaryRows(wb)["Employee"].Cell(2).GetString().Should().StartWith("'=");
    }

    [Fact]
    public async Task Months_without_a_timesheet_ask_for_nothing_in_the_report()
    {
        await _host.LoginAsync(Org.Rahul);
        var r = await Get($"/api/reports/employee-attendance.xlsx?start=2026-01-01&end=2026-03-31&employee_id={Org.Rahul.Id}");
        using var wb = new XLWorkbook(await r.Content.ReadAsStreamAsync());
        var summary = SummaryRows(wb);
        summary["Office (WFO) days"].Cell(3).GetString().Should().Be("of 24 required · Close · on target in 1 of 2 months");
        var jan = wb.Worksheet("By month").Row(2);
        (jan.Cell(1).GetString(), jan.Cell(2).GetValue<int>(), jan.Cell(3).GetValue<int>(), jan.Cell(4).GetValue<int>(), jan.Cell(5).GetString())
            .Should().Be(("Jan 2026", 22, 0, 12, "No timesheet"));
    }

    [Fact]
    public async Task Csv_export_is_scoped_audited_and_injection_safe()
    {
        using (var db = _host.Db())
        {
            db.Users.Single(u => u.Id == Org.Rahul.Id).FullName = "=HYPERLINK(\"http://evil\")";
            db.SaveChanges();
        }

        await _host.LoginAsync(Org.Priya);
        var r = await Get("/api/reports/team-attendance.csv?start=2026-03-01&end=2026-03-10");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.ToString().Should().Be("text/csv; charset=utf-8");
        r.Content.Headers.ContentDisposition!.ToString().Should().Be("attachment; filename=\"team_attendance_2026-03-01_2026-03-10.csv\"");
        var bytes = await r.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF); // BOM so Excel detects UTF-8
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        text.Should().Contain("\r\n").And.EndWith("\r\n");
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("Employee ID,Name,Department,Manager,Expected days");
        lines.Should().Contain(l => l.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", StringComparison.Ordinal));
        using var db2 = _host.Db();
        db2.AuditLogs.Count(a => a.Action == "REPORT_EXPORTED").Should().Be(1);
    }

    [Fact]
    public async Task All_report_types_and_parameter_checks()
    {
        await _host.LoginAsync(Org.Admin);
        foreach (var path in new[]
                 {
                     "monthly-attendance.csv?year=2026&month=3", "department-attendance.csv?start=2026-03-01&end=2026-03-10",
                     "exceptions.csv?start=2026-03-01&end=2026-03-10", "team-attendance.csv?start=2026-03-01&end=2026-03-10",
                     $"employee-attendance.csv?start=2026-03-01&end=2026-03-10&employee_id={Org.Rahul.Id}",
                 })
        {
            var r = await Get($"/api/reports/{path}");
            r.StatusCode.Should().Be(HttpStatusCode.OK, path);
            (await r.Content.ReadAsStringAsync()).Count(c => c == '\n').Should().BeGreaterThanOrEqualTo(1);
        }

        var wfo = await Get("/api/org/wfo/export.xlsx?month=2026-03");
        wfo.Content.Headers.ContentDisposition!.ToString().Should().Be("attachment; filename=\"wfo_report_2026-03.xlsx\"");
        using (var wb = new XLWorkbook(await wfo.Content.ReadAsStreamAsync()))
        {
            wb.Worksheets.Select(w => w.Name).Should().Equal("Summary", "Daily", "Policy");
            wb.Worksheet("Policy").Cell(2, 2).GetString().Should().Be("2026-03");
        }

        var missing = await Get("/api/reports/monthly-attendance.csv?month=13");
        missing.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await missing.Content.ReadAsStringAsync();
        body.Should().Contain("\"field\":\"query.year\",\"message\":\"Field required\"").And.Contain("Input should be less than or equal to 12");
        (await Get("/api/reports/employee-attendance.csv")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Get("/api/org/wfo/export.xlsx?month=2026-13")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public void Safe_cell()
    {
        var utc = TimeZoneInfo.Utc;
        CsvReport.SafeCell("=1+1", utc).Should().Be("'=1+1");
        CsvReport.SafeCell("+SUM(A1)", utc).Should().Be("'+SUM(A1)");
        CsvReport.SafeCell("@cmd", utc).Should().Be("'@cmd");
        CsvReport.SafeCell("-2", utc).Should().Be("'-2");
        CsvReport.SafeCell(-2, utc).Should().Be("-2");
        CsvReport.SafeCell(95.0, utc).Should().Be("95.0");
        CsvReport.SafeCell(true, utc).Should().Be("Yes");
        CsvReport.SafeCell("Normal", utc).Should().Be("Normal");
        CsvReport.SafeCell(null, utc).Should().Be("");
        CsvReport.SafeCell(new DateTimeOffset(2026, 3, 2, 4, 5, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).Should().Be("2026-03-02 09:35");
        CsvReport.Hours(-90).Should().Be("-1.50"); // text, so safe_cell gives it the apostrophe too, as Python does
    }
}
