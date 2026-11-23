using System.Text.Json;
using Ats.Api.Auth;
using Ats.Api.Common;
using Ats.Application.Admin;
using Ats.Application.Devices;
using Ats.Application.Uploads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ats.Api.Controllers;

/// <summary>Office sites and the device register (<c>routes/devices.py</c>); Wi-Fi presence is matched through these devices.</summary>
[ApiController]
[Tags("devices")]
[Authorize(Policy = SessionAuth.Admin)]
public sealed class DevicesController(DeviceService devices) : ControllerBase
{
    private const string SiteCodePattern = "^[A-Za-z0-9_-]+$";

    /// <summary>Offices (HR admin).</summary>
    [HttpGet("api/admin/sites")]
    public Task<List<OfficeSiteOut>> Sites(CancellationToken ct) => devices.SitesAsync(ct);

    [HttpPost("api/admin/sites")]
    public async Task<IActionResult> CreateSite([FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var b = new BodyReader(q, body);
        var name = b.Str("name", required: true, nullable: false, minLength: 2, maxLength: 100).Value;
        var code = b.Str("code", required: true, nullable: false, minLength: 1, maxLength: 16, pattern: SiteCodePattern).Value;
        var network = b.Str("network_name", maxLength: 64).Value;
        var address = b.Str("address", maxLength: 500).Value;
        q.ThrowIfInvalid();
        var created = await devices.CreateSiteAsync(HttpContext.Auth().User, name!, code!, network, address, HttpContext.ToRequestContext(), ct)
            .ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("api/admin/sites/{siteId}")]
    public Task<OfficeSiteOut> UpdateSite(string siteId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("site_id", siteId);
        var b = new BodyReader(q, body);
        var data = new Dictionary<string, object?>();
        AdminController.Put(data, "name", b.Str("name", minLength: 2, maxLength: 100));
        AdminController.Put(data, "code", b.Str("code", minLength: 1, maxLength: 16, pattern: SiteCodePattern));
        AdminController.Put(data, "network_name", b.Str("network_name", maxLength: 64));
        AdminController.Put(data, "address", b.Str("address", maxLength: 500));
        AdminController.Put(data, "is_active", b.Bool("is_active"));
        q.ThrowIfInvalid();
        return devices.UpdateSiteAsync(HttpContext.Auth().User, id, data, HttpContext.ToRequestContext(), ct);
    }

    /// <summary>Device register (HR admin).</summary>
    [HttpGet("api/admin/devices")]
    public Task<PagedResult<DeviceOut>> List(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (page, pageSize) = Params.Paging(q);
        var search = q.Str("search", maxLength: 100);
        var assigned = q.BoolOrNull("assigned");
        var hasMac = q.BoolOrNull("has_mac");
        var active = q.Bool("active", true);
        q.ThrowIfInvalid();
        return devices.ListDevicesAsync(search, assigned, hasMac, active, page, pageSize, ct);
    }

    /// <summary>How ready the register is for Wi-Fi matching.</summary>
    [HttpGet("api/admin/devices/summary")]
    public Task<RegisterSummary> Summary(CancellationToken ct) => devices.RegisterSummaryAsync(ct);

    private static Dictionary<string, object?> DeviceFields(BodyReader b, bool update)
    {
        var data = new Dictionary<string, object?>();
        AdminController.Put(data, "serial_number", b.Str("serial_number", maxLength: 64));
        AdminController.Put(data, "mac_address", b.Str("mac_address", maxLength: 32));
        AdminController.Put(data, "asset_tag", b.Str("asset_tag", maxLength: 64));
        AdminController.Put(data, "hostname", b.Str("hostname", maxLength: 64));
        AdminController.Put(data, "model", b.Str("model", maxLength: 120));
        AdminController.Put(data, "user_id", b.Int("user_id"));
        AdminController.Put(data, "notes", b.Str("notes", maxLength: 1000));
        if (update)
        {
            AdminController.Put(data, "is_active", b.Bool("is_active"));
        }

        return data;
    }

    [HttpPost("api/admin/devices")]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var data = DeviceFields(new BodyReader(q, body), update: false);
        q.ThrowIfInvalid();
        var created = await devices.CreateDeviceAsync(HttpContext.Auth().User, data, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("api/admin/devices/{deviceId}")]
    public Task<DeviceOut> Update(string deviceId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("device_id", deviceId);
        var data = DeviceFields(new BodyReader(q, body), update: true);
        q.ThrowIfInvalid();
        return devices.UpdateDeviceAsync(HttpContext.Auth().User, id, data, HttpContext.ToRequestContext(), ct);
    }

    /// <summary>Import an asset list or Intune export (CSV / Excel): checked first, written with apply=true.</summary>
    [HttpPost("api/admin/devices/import")]
    public async Task<DeviceImportResult> Import(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var apply = q.Bool("apply", false);
        var form = Request.HasFormContentType ? await Request.ReadFormAsync(ct).ConfigureAwait(false) : null;
        var file = form?.Files.GetFile("file");
        if (file is null)
        {
            q.BodyError("file", "Field required");
        }

        q.ThrowIfInvalid();
        var content = await WfoAdminController.ReadAtMostAsync(file!, TimesheetFiles.MaxUploadBytes + 1, ct).ConfigureAwait(false);
        var name = string.IsNullOrEmpty(file!.FileName) ? "devices" : file.FileName;
        return await devices.ImportAsync(HttpContext.Auth().User, name, content, apply, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
    }
}

/// <summary>Automatic attendance settings (<c>routes/auto_attendance.py</c>, HR admin).</summary>
[ApiController]
[Tags("automatic attendance")]
[Authorize(Policy = SessionAuth.Admin)]
public sealed class AutoAttendanceController(ConfigurationService configuration) : ControllerBase
{
    /// <summary>Rules used by the automatic attendance jobs (HR admin).</summary>
    [HttpGet("api/admin/auto-attendance/settings")]
    public Task<AutoAttendanceSettingsOut> Get(CancellationToken ct) => configuration.AutoSettingsAsync(ct);

    /// <summary>Change automatic attendance rules (HR admin, audited).</summary>
    [HttpPut("api/admin/auto-attendance/settings")]
    public Task<AutoAttendanceSettingsOut> Update([FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var b = new BodyReader(q, body);
        var data = new Dictionary<string, object?>();
        AdminController.Put(data, "auto_attendance_enabled", b.Bool("auto_attendance_enabled"));
        AdminController.Put(data, "auto_shadow_mode", b.Bool("auto_shadow_mode"));
        AdminController.Put(data, "gap_minutes", b.Int("gap_minutes", ge: 1, le: 240));
        AdminController.Put(data, "full_day_minutes", b.Int("full_day_minutes", ge: 1, le: 1440));
        AdminController.Put(data, "half_day_minutes", b.Int("half_day_minutes", ge: 0, le: 1440));
        AdminController.Put(data, "wifi_poll_minutes", b.Int("wifi_poll_minutes", ge: 1, le: 60));
        AdminController.Put(data, "presence_poll_minutes", b.Int("presence_poll_minutes", ge: 1, le: 60));
        AdminController.Put(data, "no_data_alert_minutes", b.Int("no_data_alert_minutes", ge: 5, le: 1440));
        AdminController.Put(data, "raw_retention_days", b.Int("raw_retention_days", ge: 7, le: 3650));
        var states = b.StrList("wfh_presence_states", minItems: 1, maxItems: 20);
        if (states.Value is { } list)
        {
            // The schema's validator: trimmed, blanks dropped, duplicates removed (first kept), each at most 32 characters.
            var cleaned = list.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (cleaned.Count == 0 || cleaned.Any(s => s.Length > 32))
            {
                b.ValueError("wfh_presence_states", "Give 1-20 Teams presence values of at most 32 characters");
            }

            data["wfh_presence_states"] = cleaned;
        }
        else if (states.Present)
        {
            data["wfh_presence_states"] = null;
        }

        q.ThrowIfInvalid();
        return configuration.UpdateAutoSettingsAsync(HttpContext.Auth().User, data, HttpContext.ToRequestContext(), ct);
    }
}
