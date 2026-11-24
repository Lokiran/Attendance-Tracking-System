using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ats.Application.Common;
using Ats.Application.Uploads;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Tests.Auth;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ats.Tests.Uploads;

/// <summary>
/// Timesheet uploads through the API: ports of backend/app/tests/test_wfo_upload.py and test_timesheet_files.py (synthetic
/// data only). Clock: Tuesday 10 November 2026, 12:00 IST. Staff: "Team Lead" (manager) with EMP001..EMP010 (no EMP008),
/// joined 1 January 2026, next to the AuthHost org (Admin User, Vikram, Priya, Anil, Rahul E005, Sneha E006, Arjun E007).
/// </summary>
public sealed class UploadApiTests : IDisposable
{
    private const string Url = "/api/admin/attendance-uploads";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly string[] Codes = ["EMP001", "EMP002", "EMP003", "EMP004", "EMP005", "EMP006", "EMP007", "EMP009", "EMP010"];

    private readonly AuthHost _host = new();
    private readonly Dictionary<string, User> _people = [];
    private readonly User _manager;

    public UploadApiTests()
    {
        _host.Clock.UtcNow = new DateTimeOffset(2026, 11, 10, 6, 30, 0, TimeSpan.Zero);
        using var db = _host.Db();
        _manager = new User
        {
            EmployeeCode = "TL01", FullName = "Team Lead", Email = "team.lead@test.example", PasswordHash = AuthHost.PythonHash, Role = Role.Manager,
            DateJoined = new DateOnly(2025, 1, 1),
        };
        db.Users.Add(_manager);
        db.SaveChanges();
        foreach (var code in Codes)
        {
            var u = new User
            {
                EmployeeCode = code, FullName = $"Person {code}", Email = $"person.{code.ToLowerInvariant()}@test.example",
                PasswordHash = AuthHost.PythonHash, Role = Role.Employee, ManagerId = _manager.Id, DateJoined = new DateOnly(2026, 1, 1),
            };
            db.Users.Add(u);
            db.SaveChanges();
            _people[code] = u;
        }
    }

    public void Dispose() => _host.Dispose();

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "uploads", name));

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<string> Admin() => _host.LoginAsync(_host.Org.Admin);

    private async Task<(HttpStatusCode Status, JsonElement Body)> Upload(string? csrf, string name, byte[] content, string mime = "text/csv",
                                                                       string? query = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url + (query is null ? "" : "?" + query)) { Content = form };
        if (csrf is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }

        var r = await _host.Client.SendAsync(request);
        return (r.StatusCode, await Json(r));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string? csrf, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (csrf is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }

        var r = await _host.Client.SendAsync(request);
        return (r.StatusCode, await Json(r));
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Commit(string csrf, int id, bool overwrite = false) =>
        Send(HttpMethod.Post, $"{Url}/{id}/commit", csrf, new { overwrite_overrides = overwrite });

    private async Task<JsonElement> WfoMonth(int employeeId, string month = "2026-10") =>
        await Json(await _host.Client.GetAsync(new Uri($"/api/team/wfo/employees/{employeeId}?month={month}", UriKind.Relative)));

    private static JsonElement Day(JsonElement body, string date) =>
        body.GetProperty("days").EnumerateArray().First(d => d.GetProperty("date").GetString() == date);

    /// <summary>A JSON object's members, for comparisons where Python compares dicts (key order aside).</summary>
    private static Dictionary<string, string> Pairs(JsonElement o) => o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());

    private static Dictionary<string, string> Pairs(string json) => Pairs(JsonDocument.Parse(json).RootElement);

    private static int I(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    private static string? S(JsonElement e, string name) => e.GetProperty(name).GetString();

    private int Count<T>(Func<Infrastructure.Persistence.AtsDbContext, IQueryable<T>> query)
    {
        using var db = _host.Db();
        return query(db).Count();
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void Value_parsers()
    {
        UploadValues.ParseDate("31/03/2026").Should().Be(new DateOnly(2026, 3, 31));
        UploadValues.ParseDate("31-03-2026").Should().Be(new DateOnly(2026, 3, 31));
        UploadValues.ParseDate("2026-03-31").Should().Be(new DateOnly(2026, 3, 31));
        UploadValues.ParseDate("31-Mar-2026").Should().Be(new DateOnly(2026, 3, 31));
        UploadValues.ParseDate(new DateTime(2026, 3, 31, 9, 0, 0)).Should().Be(new DateOnly(2026, 3, 31));
        UploadValues.ParseDate("03/31/2026").Should().BeNull(); // day-first only
        UploadValues.ParseHours("7.5").Should().Be(450);
        UploadValues.ParseHours(7.5).Should().Be(450);
        UploadValues.ParseHours("7:30").Should().Be(450);
        UploadValues.ParseHours("").Should().BeNull();
        UploadValues.ParseHours("08:37:00").Should().Be(517);
        FluentActions.Invoking(() => UploadValues.ParseHours("30")).Should().Throw<RowValueException>().WithMessage("hours must be between 0 and 24");
        UploadValues.ParseTime("09:30").Should().Be(new TimeOnly(9, 30));
        UploadValues.ParseTime("6:15 PM").Should().Be(new TimeOnly(18, 15));
        FluentActions.Invoking(() => UploadValues.ParseTime("25:99")).Should().Throw<RowValueException>();
    }

    [Fact]
    public void Ambiguous_dates_follow_the_month_of_the_file()
    {
        object[] values = ["15/09/2026", "30-09-2026", new DateTime(2026, 9, 18), "09-01-2026", new DateTime(2026, 1, 9), new DateTime(2026, 9, 5)];
        var reader = DateReader.ForValues(values);
        reader.Months.Should().BeEquivalentTo([(2026, 9)]);
        values.Select(reader.Read).Should().Equal(new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 18),
                                                  new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 5));
        reader.MonthFirst.Should().Be(2);
        var alone = DateReader.ForValues(["05/06/2026"]);
        alone.Read("05/06/2026").Should().Be(new DateOnly(2026, 6, 5));
        alone.Read(new DateTime(2026, 1, 9)).Should().Be(new DateOnly(2026, 1, 9));
    }

    [Fact]
    public void Csv_delimiters_and_file_checks()
    {
        var mapping = UploadParityTests.Mapping.Value;
        TimesheetFiles.ReadWorkbook("a.csv", "Employee;Date\nEMP001;2026-10-05\n"u8.ToArray(), mapping).Rows[1].Should().Equal("EMP001", "2026-10-05");
        FluentActions.Invoking(() => TimesheetFiles.ReadWorkbook("a.docx", "x"u8.ToArray(), mapping)).Should().Throw<ValidationFailedException>()
            .WithMessage("Upload a CSV, TSV, TXT, Excel (.xlsx, .xls) or PDF file");
        FluentActions.Invoking(() => TimesheetFiles.ReadWorkbook("a.csv", [], mapping)).Should().Throw<ValidationFailedException>().WithMessage("*empty*");
        FluentActions.Invoking(() => TimesheetFiles.ReadWorkbook("a.csv", new byte[5 * 1024 * 1024 + 1], mapping)).Should()
            .Throw<ValidationFailedException>().WithMessage("The file is larger than 5 MB");
        FluentActions.Invoking(() => TimesheetFiles.ReadWorkbook("t.xls", "not an excel file"u8.ToArray(), mapping)).Should()
            .Throw<ValidationFailedException>().WithMessage("This Excel (.xls) file could not be read");
    }

    [Fact]
    public async Task Header_problems_are_reported()
    {
        var csrf = await Admin();
        var (status, body) = await Upload(csrf, "x.csv", "Name,Day\nA,2026-10-05\n"u8.ToArray());
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("error").GetProperty("message").GetString().Should()
            .Be("Could not find an employee and a date column in the first rows. Found: Name, Day");
    }

    [Fact]
    public async Task Employee_and_date_only_is_an_office_presence_list()
    {
        var csrf = await Admin();
        var content = "Employee,Date\nEMP001,2026-10-05\n"u8.ToArray();
        var (status, body) = await Upload(csrf, "x.csv", content);
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var s = body.GetProperty("summary");
        s.GetProperty("presence_list").GetBoolean().Should().BeTrue();
        (S(s, "presence_mode"), I(s, "office"), I(s, "error_rows")).Should().Be(("OFFICE", 1, 0));
        (_, body) = await Upload(csrf, "x.csv", content, query: "presence_mode=WFH");
        (I(body.GetProperty("summary"), "wfh"), S(body.GetProperty("summary"), "presence_mode")).Should().Be((1, "WFH"));
        (status, _) = await Upload(csrf, "x.csv", content, query: "presence_mode=LEAVE");
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ------------------------------------------------------------------ sample files: preview and commit

    [Fact]
    public async Task Sample_csv_preview_counts_office_wfh_and_conflicts()
    {
        var csrf = await Admin();
        var (status, body) = await Upload(csrf, "wfo_attendance_sample.csv", Fixture("wfo_attendance_sample.csv"));
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var s = body.GetProperty("summary");
        (S(body, "status"), I(body, "row_count"), I(body, "error_count")).Should().Be(("PREVIEW", 182, 0));
        (I(s, "badge_rows"), I(s, "employees"), I(s, "employee_days")).Should().Be((2, 9, 180));
        I(s, "office").Should().BePositive();
        I(s, "wfh").Should().BePositive();
        s.GetProperty("by_work_mode").GetProperty("LEAVE").GetInt32().Should().Be(1);
        (I(s, "office") + I(s, "wfh") + 1).Should().Be(180);
        I(s, "conflict_count").Should().Be(2); // EMP003: HR said WFH, badge says office (8 and 15 Oct)
        s.GetProperty("conflicts").EnumerateArray().Select(c => S(c, "date")).Should().BeEquivalentTo("2026-10-08", "2026-10-15");
        I(s, "office_below_minimum").Should().Be(4); // EMP004's 3.5 h Mondays
        S(s.GetProperty("columns"), "work_mode").Should().Be("Work Mode");
        Count(db => db.AttendanceDaily).Should().Be(0); // preview applies nothing
    }

    [Fact]
    public async Task Commit_applies_and_classifies()
    {
        var csrf = await Admin();
        var (_, up) = await Upload(csrf, "s.csv", Fixture("wfo_attendance_sample.csv"));
        var (status, body) = await Commit(csrf, I(up, "id"));
        (status, S(body, "status"), I(body.GetProperty("commit_summary"), "applied_days")).Should().Be((HttpStatusCode.OK, "COMMITTED", 180));
        Count(db => db.AttendanceDaily).Should().Be(180);

        var d = Day(await WfoMonth(_people["EMP003"].Id), "2026-10-08");
        (S(d, "work_mode"), S(d, "wfo_source")).Should().Be(("OFFICE", "BADGE"));
        d.GetProperty("conflicts").GetArrayLength().Should().BePositive();
        var emp1 = await WfoMonth(_people["EMP001"].Id);
        S(Day(emp1, "2026-10-05"), "wfo_source").Should().Be("UPLOAD_COLUMN");
        I(emp1.GetProperty("summary"), "wfo_days").Should().Be(12);
        var emp4 = await WfoMonth(_people["EMP004"].Id);
        S(Day(emp4, "2026-10-05"), "work_mode").Should().Be("OFFICE");
        Day(emp4, "2026-10-05").GetProperty("is_wfo_day").GetBoolean().Should().BeFalse();
        var emp5 = await WfoMonth(_people["EMP005"].Id);
        (I(emp5.GetProperty("summary"), "wfo_days"), S(emp5.GetProperty("summary"), "compliance")).Should().Be((4, "NON_COMPLIANT"));
        var emp6 = await WfoMonth(_people["EMP006"].Id);
        S(Day(emp6, "2026-10-14"), "work_mode").Should().Be("LEAVE");
        I(emp6.GetProperty("summary"), "leave_days").Should().Be(1);
        Count(db => db.AuditLogs.Where(a => a.Action == "ATTENDANCE_UPLOAD_COMMITTED")).Should().Be(1);
        (await Commit(csrf, I(up, "id"))).Status.Should().Be(HttpStatusCode.Conflict); // only once
    }

    [Fact]
    public async Task Reupload_upserts_and_respects_manual_overrides()
    {
        var csrf = await Admin();
        var content = Fixture("wfo_attendance_sample.csv");
        await Commit(csrf, I((await Upload(csrf, "s.csv", content)).Body, "id"));
        var emp2 = _people["EMP002"];
        (await Send(HttpMethod.Put, $"/api/admin/attendance/{emp2.Id}/2026-10-06/work-mode", csrf, new { work_mode = "WFH" })).Status
            .Should().Be(HttpStatusCode.OK);

        var again = (await Upload(csrf, "s.csv", content)).Body;
        (I(again.GetProperty("summary"), "protected_overrides"), I(again.GetProperty("summary"), "new_days")).Should().Be((1, 0));
        I((await Commit(csrf, I(again, "id"))).Body.GetProperty("commit_summary"), "skipped_overrides").Should().Be(1);
        Count(db => db.AttendanceDaily).Should().Be(180); // upsert, no duplicates
        var d = Day(await WfoMonth(emp2.Id), "2026-10-06");
        (S(d, "work_mode"), S(d, "wfo_source")).Should().Be(("WFH", "MANUAL_OVERRIDE"));

        var third = (await Upload(csrf, "s.csv", content)).Body;
        I((await Commit(csrf, I(third, "id"), overwrite: true)).Body.GetProperty("commit_summary"), "overrides_overwritten").Should().Be(1);
        d = Day(await WfoMonth(emp2.Id), "2026-10-06");
        (S(d, "work_mode"), S(d, "wfo_source")).Should().Be(("OFFICE", "UPLOAD_COLUMN"));
        using var db = _host.Db();
        var cleared = db.AuditLogs.Single(a => a.Action == "WORK_MODE_OVERRIDE_CLEARED" && a.AfterValues != null);
        cleared.AfterValues!["via_upload"]!.GetValue<int>().Should().Be(I(third, "id"));
    }

    [Fact]
    public async Task Badge_excel_turns_wfh_days_into_office_with_conflicts()
    {
        var csrf = await Admin();
        await Commit(csrf, I((await Upload(csrf, "s.csv", Fixture("wfo_attendance_sample.csv"))).Body, "id"));
        var emp5 = _people["EMP005"];
        I((await WfoMonth(emp5.Id)).GetProperty("summary"), "wfo_days").Should().Be(4);

        var (status, badge) = await Upload(csrf, "wfo_badge_sample.xlsx", Fixture("wfo_badge_sample.xlsx"), Xlsx);
        status.Should().Be(HttpStatusCode.Created, badge.ToString());
        I(badge, "error_count").Should().Be(0);
        I(badge.GetProperty("summary"), "badge_rows").Should().Be(I(badge, "row_count"));
        I(badge.GetProperty("summary"), "conflict_count").Should().Be(24); // 3 days x 4 weeks x 2 people
        await Commit(csrf, I(badge, "id"));
        var body = await WfoMonth(emp5.Id);
        var thursday = Day(body, "2026-10-08");
        (S(thursday, "work_mode"), S(thursday, "wfo_source")).Should().Be(("OFFICE", "BADGE"));
        thursday.GetProperty("conflicts")[0].GetString().Should().Contain("UPLOAD_COLUMN said WFH");
        I(body.GetProperty("summary"), "wfo_days").Should().Be(16);
        S(Day(body, "2026-10-06"), "wfo_source").Should().Be("UPLOAD_COLUMN");
    }

    [Fact]
    public async Task Row_errors_are_listed_and_skipped()
    {
        const string csv = "Employee,Date,Mode,Hours\n"
                           + "EMP001,05/10/2026,WFO,8\n"
                           + "NOPE,05/10/2026,WFO,8\n"
                           + "EMP001,2026/31/10,WFO,8\n"
                           + "EMP002,05/10/2026,Hybrid,8\n"
                           + "EMP003,05/10/2026,WFO,30\n"
                           + "EMP004,20/11/2026,WFO,8\n"
                           + "EMP004,20/11/2026,Leave,\n"
                           + "emp005,06/10/2026,Remote,7.5\n";
        var csrf = await Admin();
        var body = (await Upload(csrf, "e.csv", Encoding.UTF8.GetBytes(csv))).Body;
        var errors = body.GetProperty("summary").GetProperty("errors").EnumerateArray().ToDictionary(e => I(e, "row"), e => S(e, "error")!);
        errors.Keys.Should().BeEquivalentTo([3, 4, 5, 6, 7]);
        errors[3].Should().Be("unknown employee 'NOPE' (not in the app yet)");
        errors[4].Should().Be("date '2026/31/10' is not a date (use 31/03/2026 or 2026-03-31)");
        errors[5].Should().Be("unknown work mode 'Hybrid' (accepted values are in config/wfo_mapping.json)");
        errors[6].Should().Be("hours must be between 0 and 24");
        errors[7].Should().Be("future dates are only accepted for leave, holiday or weekly off");
        I((await Commit(csrf, I(body, "id"))).Body.GetProperty("commit_summary"), "applied_days").Should().Be(3);
        Count(db => db.AttendanceDaily).Should().Be(3);
    }

    [Fact]
    public async Task Upload_access_and_lifecycle()
    {
        var content = "Employee,Date,Mode\nEMP001,05/10/2026,WFO\n"u8.ToArray();
        (await Upload(await _host.LoginAsync(_manager), "s.csv", content)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await Upload(await _host.LoginAsync(_people["EMP001"]), "s.csv", content)).Status.Should().Be(HttpStatusCode.Forbidden);
        await Admin();
        (await Upload(null, "s.csv", "x"u8.ToArray())).Status.Should().Be(HttpStatusCode.Forbidden); // no CSRF token
        var csrf = await Admin();
        var up = (await Upload(csrf, "s.csv", content)).Body;
        S((await Send(HttpMethod.Post, $"{Url}/{I(up, "id")}/discard", csrf)).Body, "status").Should().Be("DISCARDED");
        (await Commit(csrf, I(up, "id"))).Status.Should().Be(HttpStatusCode.Conflict);
        var listed = await Json(await _host.Client.GetAsync(new Uri(Url, UriKind.Relative)));
        (I(listed[0], "id"), S(listed[0], "uploaded_by_name")).Should().Be((I(up, "id"), "Admin User"));
        (await _host.Client.GetAsync(new Uri($"{Url}/999", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Missing_file_and_bad_query_are_422_in_fastapi_order()
    {
        var csrf = await Admin();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}?presence_mode=NOPE") { Content = new FormUrlEncodedContent([new("x", "1")]) };
        request.Headers.Add("X-CSRF-Token", csrf);
        var r = await _host.Client.SendAsync(request);
        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var details = (await Json(r)).GetProperty("error").GetProperty("details").EnumerateArray().Select(d => S(d, "field")).ToList();
        details.Should().Equal("query.presence_mode", "file");
    }

    // ------------------------------------------------------------------ a monthly workbook

    [Fact]
    public async Task Monthly_workbook_imports_and_adds_unknown_people()
    {
        var csrf = await Admin();
        var (status, up) = await Upload(csrf, "October 2026.xlsx", Fixture("monthly_workbook.xlsx"), Xlsx);
        status.Should().Be(HttpStatusCode.Created, up.ToString());
        var s = up.GetProperty("summary");
        S(s, "sheet").Should().Be("October 2026"); // the month's sheet, not the raw extract that comes first
        s.GetProperty("sheets").EnumerateArray().Select(x => S(x, "name")).Should().Equal("Raw Data", "October 2026");
        s.GetProperty("sheets").EnumerateArray().Should().OnlyContain(x => x.GetProperty("usable").GetBoolean());
        (I(s, "rows"), I(s, "blank_rows"), I(s, "duplicate_rows")).Should().Be((9, 6, 1));
        Pairs(s.GetProperty("columns")).Should().BeEquivalentTo(Pairs("""{"employee":"Employee ID","name":"Employee","date":"Date"}"""));
        s.GetProperty("date_notes").EnumerateArray().Should().Contain(n => n.GetString()!.Contains("swapped"));
        s.GetProperty("name_mismatches").GetRawText().Should().Be("""[{"employee_ref":"EMP002","file_name":"Somebody Else","app_name":"Person EMP002"}]""");
        var intern = s.GetProperty("unknown_employees").EnumerateArray().Single();
        (S(intern, "employee_ref"), I(intern, "rows"), intern.GetProperty("can_add").GetBoolean()).Should().Be(("Intern - 10001", 3, true));
        (S(intern, "name"), intern.GetProperty("other_names").GetRawText()).Should().Be(("New Intern", """["N. Intern"]"""));
        (S(intern, "first_date"), I(up, "error_count")).Should().Be(("2026-10-21", 3));
        (I(s, "office"), S(s, "date_from")).Should().Be((5, "2026-10-01"));

        var (created, after) = await Send(HttpMethod.Post, $"{Url}/{I(up, "id")}/create-employees", csrf, new { });
        created.Should().Be(HttpStatusCode.OK, after.ToString());
        I(after, "error_count").Should().Be(0);
        after.GetProperty("summary").GetProperty("unknown_employees").GetArrayLength().Should().Be(0);
        (I(after.GetProperty("summary"), "employees_added"), I(after.GetProperty("summary"), "office")).Should().Be((1, 8));
        using (var db = _host.Db())
        {
            var added = db.Users.Single(u => u.EmployeeCode == "Intern - 10001"); // kept as written
            (added.FullName, added.Email, added.Role, added.DateJoined, added.ManagerId).Should()
                .Be(("New Intern", "intern.10001@imported.example.com", Role.Employee, new DateOnly(2026, 10, 1), (int?)null));
            db.AuditLogs.Count(a => a.Action == "USER_CREATED" && a.EntityId == added.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Should().Be(1);
        }

        (await Send(HttpMethod.Post, $"{Url}/{I(up, "id")}/create-employees", csrf, new { })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        I((await Commit(csrf, I(up, "id"))).Body.GetProperty("commit_summary"), "applied_days").Should().Be(8);
        var emp1 = await WfoMonth(_people["EMP001"].Id);
        I(emp1.GetProperty("summary"), "wfo_days").Should().Be(4);
        Day(emp1, "2026-10-01").GetProperty("is_wfo_day").GetBoolean().Should().BeTrue(); // a presence list has no hours: the day still counts
        int newId;
        using (var db = _host.Db())
        {
            newId = db.Users.Single(u => u.EmployeeCode == "Intern - 10001").Id;
        }

        I((await WfoMonth(newId)).GetProperty("summary"), "wfo_days").Should().Be(3);
        var again = (await Upload(csrf, "x.csv", "Employee,Date\nintern   - 10001,2026-10-26\n"u8.ToArray())).Body;
        I(again, "error_count").Should().Be(0);
    }

    [Fact]
    public async Task Sheet_can_be_chosen()
    {
        var csrf = await Admin();
        var content = Fixture("monthly_workbook.xlsx");
        var (status, body) = await Upload(csrf, "m.xlsx", content, Xlsx, "sheet=Raw%20Data");
        var s = body.GetProperty("summary");
        status.Should().Be(HttpStatusCode.Created);
        (S(s, "sheet"), s.GetProperty("presence_list").GetBoolean(), S(s.GetProperty("columns"), "hours")).Should().Be(("Raw Data", true, "Total Hrs"));
        (I(s, "office"), I(s, "office_below_minimum")).Should().Be((1, 0));
        (status, body) = await Upload(csrf, "m.xlsx", content, Xlsx, "sheet=Nope");
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("error").GetProperty("message").GetString().Should().Be("There is no sheet 'Nope'. Sheets: Raw Data, October 2026");
    }

    [Fact]
    public async Task Only_addable_ids_are_added()
    {
        var csrf = await Admin();
        var up = (await Upload(csrf, "x.csv", "Employee,Date\nsomeone@else.example,2026-10-05\nok-77,2026-10-05\n"u8.ToArray())).Body;
        var byRef = up.GetProperty("summary").GetProperty("unknown_employees").EnumerateArray().ToDictionary(u => S(u, "employee_ref")!);
        byRef["someone@else.example"].GetProperty("can_add").GetBoolean().Should().BeFalse();
        byRef["ok-77"].GetProperty("can_add").GetBoolean().Should().BeTrue();
        var after = (await Send(HttpMethod.Post, $"{Url}/{I(up, "id")}/create-employees", csrf, new { employee_refs = new[] { "OK-77" } })).Body;
        I(after, "error_count").Should().Be(1);
        after.GetProperty("summary").GetProperty("unknown_employees").EnumerateArray().Select(u => S(u, "employee_ref")).Should().Equal("someone@else.example");
        var managerCsrf = await _host.LoginAsync(_manager);
        (await Send(HttpMethod.Post, $"{Url}/{I(up, "id")}/create-employees", managerCsrf, new { })).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ month-to-month: headers, IDs, joining dates

    [Fact]
    public async Task Column_names_ignore_punctuation()
    {
        var body = (await Upload(await Admin(), "a.csv", "Emp. Code,Emp. Name,Date\nEMP001,Person EMP001,05/10/2026\n"u8.ToArray())).Body;
        I(body, "error_count").Should().Be(0);
        body.GetProperty("summary").GetProperty("columns").GetRawText().Should().Be("""{"employee":"Emp. Code","name":"Emp. Name","date":"Date"}""");
    }

    [Fact]
    public async Task Times_from_the_swipe_sheet_and_a_changed_id()
    {
        var csrf = await Admin();
        var (status, up) = await Upload(csrf, "October 2026.xlsx", Fixture("two_sheet_workbook.xlsx"), Xlsx);
        status.Should().Be(HttpStatusCode.Created, up.ToString());
        var s = up.GetProperty("summary");
        (S(s, "sheet"), I(up, "error_count")).Should().Be(("October 2026", 0));
        s.GetProperty("hours_from").GetRawText().Should().Be("""{"sheets":["TS Raw Data"],"days":1}"""); // matched by name: the IDs differ
        (S(s.GetProperty("columns"), "in_time"), S(s.GetProperty("columns"), "hours")).Should().Be(("Time In", "Total Hrs"));
        s.GetProperty("unknown_employees").GetArrayLength().Should().Be(0);
        s.GetProperty("matched_by_name").GetRawText().Should()
            .Be("""[{"employee_ref":"Intern-9","name":"Person EMP002","app_code":"EMP002","app_name":"Person EMP002","rows":1}]""");
        var users = Count(db => db.Users);
        await Commit(csrf, I(up, "id"));
        using var db = _host.Db();
        var rows = db.AttendanceDaily.ToDictionary(r => (r.EmployeeId, r.WorkDate));
        rows[(_people["EMP001"].Id, new DateOnly(2026, 10, 1))].UploadMinutes.Should().Be(510);
        rows[(_people["EMP001"].Id, new DateOnly(2026, 10, 2))].UploadMinutes.Should().BeNull();
        rows.Should().ContainKey((_people["EMP002"].Id, new DateOnly(2026, 10, 2)));
        db.Users.Count().Should().Be(users); // nobody added twice
    }

    [Fact]
    public async Task A_shared_name_is_not_guessed()
    {
        using (var db = _host.Db())
        {
            db.Users.Add(new User
            {
                EmployeeCode = "TW01", FullName = "Person EMP004", Email = "person.twin@test.example", PasswordHash = AuthHost.PythonHash,
                Role = Role.Employee, DateJoined = new DateOnly(2025, 1, 1),
            });
            db.SaveChanges();
        }

        var s = (await Upload(await Admin(), "a.csv", "Emp Code,Emp Name,Date\nIntern-9,Person EMP004,05/10/2026\n"u8.ToArray())).Body.GetProperty("summary");
        s.GetProperty("matched_by_name").GetArrayLength().Should().Be(0);
        s.GetProperty("unknown_employees").EnumerateArray().Select(u => S(u, "employee_ref")).Should().Equal("Intern-9");
    }

    [Fact]
    public async Task A_name_in_the_id_column_is_linked_to_that_person_not_added()
    {
        var csrf = await Admin();
        var users = Count(db => db.Users);
        var up = (await Upload(csrf, "a.csv", "Employee,Date,Time In,Time Out\nPerson EMP002,05/10/2026,09:30,17:30\n"u8.ToArray())).Body;
        var s = up.GetProperty("summary");
        (s.GetProperty("unknown_employees").GetArrayLength(), I(up, "error_count")).Should().Be((0, 0));
        S(s.GetProperty("matched_by_name")[0], "app_code").Should().Be("EMP002");
        await Commit(csrf, I(up, "id"));
        using var db = _host.Db();
        var row = db.AttendanceDaily.Single(r => r.WorkDate == new DateOnly(2026, 10, 5));
        (row.EmployeeId, row.UploadMinutes).Should().Be((_people["EMP002"].Id, 480));
        db.Users.Count().Should().Be(users);
    }

    [Fact]
    public async Task An_earlier_month_moves_a_guessed_joining_date_back()
    {
        var csrf = await Admin();
        var oct = (await Upload(csrf, "oct.csv", "Emp Code,Emp Name,Date\nT-1,New Person,05/10/2026\n"u8.ToArray())).Body;
        await Send(HttpMethod.Post, $"{Url}/{I(oct, "id")}/create-employees", csrf, new { });
        await Commit(csrf, I(oct, "id"));
        int newId;
        using (var db = _host.Db())
        {
            var added = db.Users.Single(u => u.EmployeeCode == "T-1");
            added.DateJoined.Should().Be(new DateOnly(2026, 10, 1));
            newId = added.Id;
        }

        var sep = (await Upload(csrf, "sep.csv", "Emp Code,Emp Name,Date\nT-1,New Person,15/09/2026\n"u8.ToArray())).Body;
        I(sep, "error_count").Should().Be(0);
        sep.GetProperty("summary").GetProperty("joining_dates_moved").GetRawText().Should()
            .Be("""[{"employee_code":"T-1","full_name":"New Person","from":"2026-10-01","to":"2026-09-01"}]""");
        I(sep.GetProperty("summary"), "office").Should().Be(1); // the preview already counts the day
        var done = (await Commit(csrf, I(sep, "id"))).Body;
        I(done.GetProperty("commit_summary"), "joining_dates_moved").Should().Be(1);
        using (var db = _host.Db())
        {
            db.Users.Single(u => u.Id == newId).DateJoined.Should().Be(new DateOnly(2026, 9, 1));
            db.AuditLogs.Count(a => a.Action == "USER_UPDATED" && a.EntityId == newId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Should().Be(1);
        }

        I((await WfoMonth(newId, "2026-09")).GetProperty("summary"), "wfo_days").Should().Be(1);
    }

    [Fact]
    public async Task A_joining_date_from_hr_still_applies()
    {
        var body = (await Upload(await Admin(), "a.csv", "Employee,Date\nEMP001,15/12/2025\n"u8.ToArray())).Body;
        var error = body.GetProperty("summary").GetProperty("errors").EnumerateArray().Single();
        S(error, "error").Should().Be("date is before Person EMP001 joined (01 Jan 2026): check the employee ID, or change the joining date on the Employees page");
        body.GetProperty("summary").GetProperty("joining_dates_moved").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_file_without_hours_keeps_hours_from_an_earlier_file()
    {
        var csrf = await Admin();
        await Commit(csrf, I((await Upload(csrf, "a.csv", "Employee,Date,Hours\nEMP001,05/10/2026,8.5\nEMP001,06/10/2026,7\n"u8.ToArray())).Body, "id"));
        var daysOnly = (await Upload(csrf, "b.csv", "Employee,Date\nEMP001,05/10/2026\nEMP001,07/10/2026\n"u8.ToArray())).Body;
        I(daysOnly.GetProperty("summary"), "office_below_minimum").Should().Be(0);
        await Commit(csrf, I(daysOnly, "id"));
        Dictionary<DateOnly, int?> Minutes()
        {
            using var db = _host.Db();
            return db.AttendanceDaily.ToDictionary(r => r.WorkDate, r => r.UploadMinutes);
        }

        var rows = Minutes();
        (rows[new DateOnly(2026, 10, 5)], rows[new DateOnly(2026, 10, 6)], rows[new DateOnly(2026, 10, 7)]).Should().Be((510, 420, (int?)null));
        var newer = (await Upload(csrf, "c.csv", "Employee,Date,Mode,Hours\nEMP001,05/10/2026,WFO,6\nEMP001,06/10/2026,WFH,\n"u8.ToArray())).Body;
        await Commit(csrf, I(newer, "id"));
        rows = Minutes();
        (rows[new DateOnly(2026, 10, 5)], rows[new DateOnly(2026, 10, 6)]).Should().Be((360, (int?)null));
    }

    [Fact]
    public async Task Pdf_timesheet_imports_like_a_sheet()
    {
        var (status, body) = await Upload(await Admin(), "October 2026.pdf", Fixture("october_sheet.pdf"), "application/pdf");
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var s = body.GetProperty("summary");
        (s.GetProperty("presence_list").GetBoolean(), I(s, "error_rows"), I(s, "office")).Should().Be((true, 0, 5));
        Pairs(s.GetProperty("columns")).Should().BeEquivalentTo(Pairs("""{"employee":"Employee ID","name":"Employee","date":"Date"}"""));
        s.GetProperty("date_notes").EnumerateArray().Should().Contain(n => n.GetString()!.Contains("swapped"));
        (S(s, "date_from"), S(s, "date_to")).Should().Be(("2026-10-01", "2026-10-16"));
        (I(body, "error_count"), s.GetProperty("name_mismatches").GetArrayLength()).Should().Be((0, 0));
    }
}
