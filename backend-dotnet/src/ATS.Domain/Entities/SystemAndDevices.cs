using System.Text.Json.Nodes;

namespace Ats.Domain.Entities;

/// <summary>Who changed what (passwords, tokens and secrets are stripped before writing).</summary>
public class AuditLog : ICreatedAt
{
    public long Id { get; set; }

    public int? ActorUserId { get; set; }

    public string Action { get; set; } = "";

    public string EntityType { get; set; } = "";

    public string? EntityId { get; set; }

    public JsonObject? BeforeValues { get; set; }

    public JsonObject? AfterValues { get; set; }

    public string? RequestId { get; set; }

    public string? IpAddress { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Server-side sign-in session. Only an HMAC of the cookie token is stored.</summary>
public class UserSession
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public string TokenHash { get; set; } = "";

    public string CsrfToken { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }
}

public class LoginAttempt : ICreatedAt
{
    public long Id { get; set; }

    public string Email { get; set; } = "";

    public string? IpAddress { get; set; }

    public bool Succeeded { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An office location with its own Wi-Fi network. Name and code are unique ignoring case.</summary>
public class OfficeSite : ITimestamped
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Short label such as "BLR-1".</summary>
    public string Code { get; set; } = "";

    /// <summary>The Wi-Fi network name (SSID) the office's logs use.</summary>
    public string? NetworkName { get; set; }

    public string? Address { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A device in the register: who it belongs to and how the Wi-Fi logs recognise it. Serial numbers are stored
/// upper-case, MAC addresses as aa:bb:cc:dd:ee:ff.
/// </summary>
public class Device : ITimestamped
{
    public int Id { get; set; }

    public string? SerialNumber { get; set; }

    public string? AssetTag { get; set; }

    public string? MacAddress { get; set; }

    public string? Hostname { get; set; }

    public string? Model { get; set; }

    public int? UserId { get; set; }

    public User? User { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>MANUAL, IMPORT or LEGACY.</summary>
    public string Source { get; set; } = "MANUAL";

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
