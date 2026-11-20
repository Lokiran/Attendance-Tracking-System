namespace Ats.Application.Common;

/// <summary>Who asked, for the audit log (<c>app/core/context.py</c>).</summary>
public sealed record RequestContext(string? RequestId = null, string? IpAddress = null, string? UserAgent = null);
