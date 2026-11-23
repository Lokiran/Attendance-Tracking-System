using Ats.Api.Auth;
using Ats.Api.Common;
using Ats.Application.Common;
using Ats.Application.Uploads;
using Ats.Application.Wfo;
using Ats.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ats.Api.Controllers;

/// <summary>
/// WFO administration (<c>routes/wfo.py</c>): day overrides, the WFO policy and timesheet uploads (preview, add unknown
/// people, commit, discard). Admin only; every change is audited.
/// </summary>
[ApiController]
[Tags("wfo")]
[Authorize(Policy = SessionAuth.Admin)]
public sealed class WfoAdminController(WfoService wfo, WfoUploadService uploads) : ControllerBase
{
    /// <summary>Override a day's work mode (admin, audited).</summary>
    [HttpPut("api/admin/attendance/{employeeId}/{workDate}/work-mode")]
    public async Task<OverrideOut> SetOverride(string employeeId, string workDate, [FromBody] OverrideRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var q = new QueryReader(Request);
        var id = q.PathInt("employee_id", employeeId);
        var day = q.PathDate("work_date", workDate);
        q.ThrowIfInvalid();
        var employee = await wfo.EmployeeAsync(id, ct).ConfigureAwait(false);
        return await wfo.SetOverrideAsync(HttpContext.Auth().User, employee, day, EnumText.Parse<WorkMode>(body.WorkMode!), body.Note,
                                          HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
    }

    /// <summary>Remove a day's override (admin, audited).</summary>
    [HttpDelete("api/admin/attendance/{employeeId}/{workDate}/work-mode")]
    public async Task<OverrideOut> ClearOverride(string employeeId, string workDate, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("employee_id", employeeId);
        var day = q.PathDate("work_date", workDate);
        q.ThrowIfInvalid();
        var employee = await wfo.EmployeeAsync(id, ct).ConfigureAwait(false);
        return await wfo.ClearOverrideAsync(HttpContext.Auth().User, employee, day, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
    }

    /// <summary>Change the WFO policy (admin, audited).</summary>
    [HttpPut("api/admin/wfo/policy")]
    public Task<WfoPolicyOut> UpdatePolicy([FromBody] WfoPolicyUpdate body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var values = new Dictionary<string, int?>
        {
            ["min_days_per_week"] = body.MinDaysPerWeek, ["min_days_per_month"] = body.MinDaysPerMonth, ["min_office_minutes"] = body.MinOfficeMinutes,
        };
        return wfo.UpdatePolicyAsync(HttpContext.Auth().User, values, HttpContext.ToRequestContext(), ct);
    }

    /// <summary>Upload an attendance / badge file (CSV, Excel or PDF) and get a preview; nothing is applied yet.</summary>
    [HttpPost("api/admin/attendance-uploads")]
    [ProducesResponseType<UploadOut>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var sheet = q.Str("sheet", maxLength: 100);
        var presenceMode = q.Enum<WorkMode>("presence_mode") ?? WorkMode.Office;
        var form = Request.HasFormContentType ? await Request.ReadFormAsync(ct).ConfigureAwait(false) : null;
        var file = form?.Files.GetFile("file");
        if (file is null)
        {
            q.BodyError("file", "Field required");
        }

        q.ThrowIfInvalid();
        var content = await ReadAtMostAsync(file!, TimesheetFiles.MaxUploadBytes + 1, ct).ConfigureAwait(false); // one byte over is enough to refuse
        var name = string.IsNullOrEmpty(file!.FileName) ? "upload" : file.FileName;
        var result = await uploads.PreviewAsync(HttpContext.Auth().User, name, content, HttpContext.ToRequestContext(), sheet, presenceMode, ct)
            .ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    internal static async Task<byte[]> ReadAtMostAsync(IFormFile file, int limit, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var buffer = new byte[Math.Min(limit, Math.Max(0, (int)Math.Min(file.Length, int.MaxValue)))];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == buffer.Length ? buffer : buffer[..total];
    }

    /// <summary>Recent uploads (admin).</summary>
    [HttpGet("api/admin/attendance-uploads")]
    public Task<List<UploadOut>> Recent(CancellationToken ct) => uploads.RecentAsync(ct: ct);

    [HttpGet("api/admin/attendance-uploads/{uploadId}")]
    public Task<UploadOut> Get(string uploadId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("upload_id", uploadId);
        q.ThrowIfInvalid();
        return uploads.GetAsync(id, ct);
    }

    /// <summary>Apply a previewed upload (upserts per employee and date).</summary>
    [HttpPost("api/admin/attendance-uploads/{uploadId}/commit")]
    public Task<UploadOut> Commit(string uploadId, [FromBody] CommitRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var q = new QueryReader(Request);
        var id = q.PathInt("upload_id", uploadId);
        q.ThrowIfInvalid();
        return uploads.CommitAsync(HttpContext.Auth().User, id, body.OverwriteOverrides, HttpContext.ToRequestContext(), ct);
    }

    [HttpPost("api/admin/attendance-uploads/{uploadId}/discard")]
    public Task<UploadOut> Discard(string uploadId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("upload_id", uploadId);
        q.ThrowIfInvalid();
        return uploads.DiscardAsync(HttpContext.Auth().User, id, HttpContext.ToRequestContext(), ct);
    }

    /// <summary>Add the upload's unknown employee IDs as employees, then re-check its rows.</summary>
    [HttpPost("api/admin/attendance-uploads/{uploadId}/create-employees")]
    public Task<UploadOut> CreateEmployees(string uploadId, [FromBody] CreateEmployeesRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var q = new QueryReader(Request);
        var id = q.PathInt("upload_id", uploadId);
        q.ThrowIfInvalid();
        return uploads.CreateEmployeesAsync(HttpContext.Auth().User, id, body.EmployeeRefs, HttpContext.ToRequestContext(), ct);
    }
}
