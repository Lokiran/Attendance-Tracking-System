using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ats.Application.Devices;
using Ats.Application.Uploads;
using Ats.Tests.Auth;
using Ats.Tests.Uploads;
using FluentAssertions;

namespace Ats.Tests.Admin;

/// <summary>Office sites and the device register (port of backend/app/tests/test_devices.py, synthetic data only).</summary>
public sealed class DeviceTests : IDisposable
{
    private const string Dev = "/api/admin/devices";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly AuthHost _host = new();

    public void Dispose() => _host.Dispose();

    private Auth.Org Org => _host.Org;

    [Theory]
    [InlineData("AA-BB-CC-DD-EE-01", "aa:bb:cc:dd:ee:01")]
    [InlineData("aabb.ccdd.ee01", "aa:bb:cc:dd:ee:01")]
    [InlineData("AABBCCDDEE01", "aa:bb:cc:dd:ee:01")]
    [InlineData(" aa:bb:cc:dd:ee:01 ", "aa:bb:cc:dd:ee:01")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Mac_addresses_are_normalised(string? raw, string? mac) => DeviceService.NormalizeMac(raw).Should().Be(mac);

    [Theory]
    [InlineData("AA:BB:CC")]
    [InlineData("zz:bb:cc:dd:ee:ff")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF-FF-FF-FF-FF-FF")]
    public void Bad_mac_addresses_are_refused(string raw) =>
        FluentActions.Invoking(() => DeviceService.NormalizeMac(raw)).Should().Throw<RowValueException>().WithMessage("*is not a MAC address*");

    [Fact]
    public void Random_addresses_and_junk_serials()
    {
        DeviceService.IsRandomMac("da:a1:19:00:00:01").Should().BeTrue();
        DeviceService.IsRandomMac("02:00:00:00:00:01").Should().BeTrue();
        DeviceService.IsRandomMac("00:1b:63:00:00:01").Should().BeFalse();
        DeviceService.IsRandomMac(null).Should().BeFalse();
        DeviceService.NormalizeSerial("  pf3a  9b2c ").Should().Be("PF3A 9B2C");
        DeviceService.NormalizeSerial("To be filled by O.E.M.").Should().BeNull();
        DeviceService.NormalizeSerial("N/A").Should().BeNull();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Import(string csrf, string name, byte[] content, string mime, bool apply = false)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Dev}/import{(apply ? "?apply=true" : "")}") { Content = form };
        request.Headers.Add("X-CSRF-Token", csrf);
        var r = await _host.Client.SendAsync(request);
        return (r.StatusCode, await WriteClient.Json(r));
    }

    private async Task<JsonElement> Get(string path) => await WriteClient.Json(await _host.Client.GetAsync(new Uri(path, UriKind.Relative)));

    [Fact]
    public async Task Offices_are_admin_only_unique_and_audited()
    {
        await _host.LoginAsync(Org.Rahul);
        (await _host.Client.GetAsync(new Uri("/api/admin/sites", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, site) = await _host.Send(HttpMethod.Post, "/api/admin/sites", csrf, new { name = "Head Office", code = "ho-1", network_name = "Corp-HO" });
        status.Should().Be(HttpStatusCode.Created, site.ToString());
        (site.GetProperty("code").GetString(), site.GetProperty("is_active").GetBoolean()).Should().Be(("HO-1", true));
        var (dupName, problem) = await _host.Send(HttpMethod.Post, "/api/admin/sites", csrf, new { name = "head office", code = "X" });
        (dupName, problem.GetProperty("error").GetProperty("message").GetString()).Should().Be((HttpStatusCode.Conflict, "Another office already uses the name 'head office'"));
        (await _host.Send(HttpMethod.Post, "/api/admin/sites", csrf, new { name = "Branch", code = "HO-1" })).Status.Should().Be(HttpStatusCode.Conflict);
        var (_, updated) = await _host.Send(HttpMethod.Patch, $"/api/admin/sites/{site.GetProperty("id").GetInt32()}", csrf, new { network_name = "Corp-HO-5G", is_active = false });
        (updated.GetProperty("network_name").GetString(), updated.GetProperty("is_active").GetBoolean()).Should().Be(("Corp-HO-5G", false));
        (await Get("/api/admin/sites")).EnumerateArray().Select(s => s.GetProperty("name").GetString()).Should().Equal("Head Office");
        using var db = _host.Db();
        db.AuditLogs.Where(a => a.EntityType == "office_site").Select(a => a.Action).Distinct().Should().BeEquivalentTo("OFFICE_SITE_CREATED", "OFFICE_SITE_UPDATED");
    }

    [Fact]
    public async Task Devices_are_created_checked_and_listed()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, laptop) = await _host.Send(HttpMethod.Post, Dev, csrf, new { serial_number = "pf3a9b2c", user_id = Org.Rahul.Id, model = "ThinkPad" });
        status.Should().Be(HttpStatusCode.Created, laptop.ToString());
        (laptop.GetProperty("serial_number").GetString(), laptop.GetProperty("mac_address").ValueKind).Should().Be(("PF3A9B2C", JsonValueKind.Null));
        (laptop.GetProperty("user").GetProperty("full_name").GetString(), laptop.GetProperty("source").GetString()).Should().Be(("Rahul Reddy", "MANUAL"));
        (await _host.Send(HttpMethod.Post, Dev, csrf, new { serial_number = "PF3A9B2C" })).Status.Should().Be(HttpStatusCode.Conflict);
        (await _host.Send(HttpMethod.Post, Dev, csrf, new { mac_address = "nope" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await _host.Send(HttpMethod.Post, Dev, csrf, new { model = "No identifier" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await _host.Send(HttpMethod.Post, Dev, csrf, new { serial_number = "X1", user_id = 99999 })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        var id = laptop.GetProperty("id").GetInt32();
        var (_, withMac) = await _host.Send(HttpMethod.Patch, $"{Dev}/{id}", csrf, new { mac_address = "DA-A1-19-00-00-01" });
        (withMac.GetProperty("mac_address").GetString(), withMac.GetProperty("random_mac").GetBoolean()).Should().Be(("da:a1:19:00:00:01", true));
        await _host.Send(HttpMethod.Post, Dev, csrf, new { serial_number = "SN-2", mac_address = "00:1b:63:00:00:02" });
        (await _host.Send(HttpMethod.Post, Dev, csrf, new { serial_number = "SN-3", mac_address = "00-1B-63-00-00-02" })).Status.Should().Be(HttpStatusCode.Conflict);
        (await Get($"{Dev}?search=rahul")).GetProperty("items").EnumerateArray().Select(d => d.GetProperty("serial_number").GetString()).Should().Equal("PF3A9B2C");
        (await Get($"{Dev}?search=001b63")).GetProperty("items").EnumerateArray().Select(d => d.GetProperty("serial_number").GetString()).Should().Equal("SN-2");
        (await Get($"{Dev}?assigned=false")).GetProperty("total").GetInt32().Should().Be(1);
        // TODO(parity) P14: removing the owner answers with the owner from before the change, as Python does.
        var (_, unassigned) = await _host.Send(HttpMethod.Patch, $"{Dev}/{id}", csrf, new { user_id = (int?)null });
        unassigned.GetProperty("user").GetProperty("full_name").GetString().Should().Be("Rahul Reddy");
        (await Get($"{Dev}?assigned=false")).GetProperty("total").GetInt32().Should().Be(2);
        await _host.Send(HttpMethod.Patch, $"{Dev}/{id}", csrf, new { is_active = false });
        (await Get(Dev)).GetProperty("total").GetInt32().Should().Be(1);
        (await Get($"{Dev}?active=false")).GetProperty("total").GetInt32().Should().Be(1);
        using (var db = _host.Db())
        {
            db.AuditLogs.Count(a => a.EntityType == "device").Should().Be(5);
        }

        await _host.LoginAsync(Org.Priya);
        (await _host.Client.GetAsync(new Uri(Dev, UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Asset_list_then_intune_export_fill_the_register()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var assets = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "uploads", "asset_list.xlsx"));
        var (status, s) = await Import(csrf, "assets.xlsx", assets, Xlsx);
        status.Should().Be(HttpStatusCode.OK, s.ToString());
        (s.GetProperty("applied").GetBoolean(), s.GetProperty("sheet").GetString()).Should().Be((false, "Assets"));
        s.GetProperty("columns").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()).Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["asset_tag"] = "Asset Tag", ["serial"] = "Serial No.", ["employee"] = "Emp. ID", ["name"] = "Employee Name", ["model"] = "Model",
        });
        (s.GetProperty("rows").GetInt32(), s.GetProperty("created").GetInt32(), s.GetProperty("errors").GetInt32()).Should().Be((5, 3, 2));
        s.GetProperty("unknown_owners").GetRawText().Should().Be("""[{"written":"NOPE-9","rows":1}]""");
        var problems = s.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("row").GetInt32(),
                                                                            i => i.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!).ToList());
        problems[6].Should().Equal("no serial number or MAC address");
        problems[8][0].Should().Contain("repeated (first on row 5)");
        using (var db = _host.Db())
        {
            db.Devices.Count().Should().Be(0); // a check writes nothing
        }

        var (_, done) = await Import(csrf, "assets.xlsx", assets, Xlsx, apply: true);
        (done.GetProperty("applied").GetBoolean(), done.GetProperty("created").GetInt32()).Should().Be((true, 3));
        using (var db = _host.Db())
        {
            db.Devices.ToDictionary(d => d.SerialNumber!, d => d.UserId).Should().BeEquivalentTo(new Dictionary<string, int?>
            {
                ["PF3A9B2C"] = Org.Rahul.Id, ["PF3A9B2D"] = Org.Sneha.Id, ["PF3A9B2E"] = null,
            });
        }

        var intune = Encoding.UTF8.GetBytes("Device name,Serial number,Wi-Fi MAC,Primary user UPN,Model\r\n"
                                            + $"LT-RAHUL,PF3A9B2C,00:1B:63:AA:00:01,{Org.Rahul.Email},ThinkPad E14\r\n"
                                            + $"LT-SNEHA,PF3A9B2D,DAA119AA0002,{Org.Sneha.Email},\r\n"
                                            + $"LT-ARJUN,NEW-SN-1,00-1B-63-AA-00-01,{Org.Arjun.Email},\r\n"
                                            + $"LT-NEW,NEW-SN-2,,{Org.Arjun.Email.ToUpperInvariant()},Latitude 5440\r\n");
        (_, s) = await Import(csrf, "intune.csv", intune, "text/csv", apply: true);
        (s.GetProperty("updated").GetInt32(), s.GetProperty("created").GetInt32(), s.GetProperty("errors").GetInt32(), s.GetProperty("random_mac").GetInt32())
            .Should().Be((2, 1, 1, 1));
        var items = s.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("row").GetInt32());
        items[2].GetProperty("changes").GetRawText().Should().Be("""["hostname","mac_address"]""");
        items[2].GetProperty("owner").GetString().Should().Be("Rahul Reddy");
        items[3].GetProperty("problems")[0].GetString().Should().Contain("private (random)");
        items[4].GetProperty("problems").GetRawText().Should().Be("""["MAC address repeated (first on row 2)"]""");
        using (var db = _host.Db())
        {
            var rahulLaptop = db.Devices.Single(d => d.SerialNumber == "PF3A9B2C");
            (rahulLaptop.MacAddress, rahulLaptop.Model).Should().Be(("00:1b:63:aa:00:01", "ThinkPad E14"));
            db.Devices.Single(d => d.SerialNumber == "NEW-SN-2").UserId.Should().Be(Org.Arjun.Id);
        }

        var clash = Encoding.UTF8.GetBytes($"Serial number,Wi-Fi MAC,Primary user UPN\r\nOTHER-SN,00:1B:63:AA:00:01,{Org.Arjun.Email}\r\n");
        (_, s) = await Import(csrf, "clash.csv", clash, "text/csv");
        s.GetProperty("errors").GetInt32().Should().Be(1);
        s.GetProperty("items")[0].GetProperty("problems")[0].GetString().Should().Contain("already belongs to serial PF3A9B2C");

        var summary = await Get($"{Dev}/summary");
        summary.GetRawText().Should().Be("""{"devices":4,"with_mac":2,"without_mac":2,"unassigned":1,"random_mac":1,"people_tracked":7,"people_with_device":2,"sites":0}""");
        using (var db = _host.Db())
        {
            db.AuditLogs.Count(a => a.Action == "DEVICES_IMPORTED").Should().Be(2);
        }
    }

    [Fact]
    public async Task Import_refuses_files_without_the_columns_it_needs()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, body) = await Import(csrf, "x.csv", "Name,Department\r\nRahul,AI\r\n"u8.ToArray(), "text/csv");
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("error").GetProperty("message").GetString().Should().Contain("serial number or MAC address column");
        var (pdf, problem) = await Import(csrf, "x.pdf", "%PDF-1.4"u8.ToArray(), "application/pdf");
        (pdf, problem.GetProperty("error").GetProperty("message").GetString()).Should()
            .Be((HttpStatusCode.UnprocessableEntity, "Upload the device list as CSV, TSV, TXT, Excel (.xlsx, .xls)"));
        csrf = await _host.LoginAsync(Org.Rahul);
        (await Import(csrf, "x.csv", "a"u8.ToArray(), "text/csv")).Status.Should().Be(HttpStatusCode.Forbidden);
    }
}
