using Ats.Application.Common;
using Ats.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;

namespace Ats.Api.Controllers;

/// <summary>Liveness and database connectivity (<c>backend/app/api/routes/health.py</c>).</summary>
[ApiController]
[Route("api/health")]
[Tags("health")]
public sealed class HealthController(IClock clock, IDatabaseProbe probe) : ControllerBase
{
    public sealed record HealthOut(string Status, DateTimeOffset Time);

    public sealed record DatabaseHealthOut(string Status, string Database);

    /// <summary>Liveness.</summary>
    [HttpGet]
    public HealthOut Get() => new("ok", clock.UtcNow);

    /// <summary>Database connectivity.</summary>
    /// <response code="503">The database cannot be reached (plain body, not the error envelope).</response>
    [HttpGet("db")]
    [ProducesResponseType<DatabaseHealthOut>(StatusCodes.Status200OK)]
    [ProducesResponseType<DatabaseHealthOut>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Database(CancellationToken cancellationToken)
    {
        if (!await probe.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new DatabaseHealthOut("unavailable", "unreachable"));
        }

        return Ok(new DatabaseHealthOut("ok", probe.DialectName));
    }
}
