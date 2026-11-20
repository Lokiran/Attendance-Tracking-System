using Ats.Application.Audit;
using Ats.Application.Common;
using Ats.Application.Data;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Auth;

/// <summary>A refused Microsoft sign-in. <see cref="Code"/> is shown to the login page; no details are leaked.</summary>
public sealed class EntraSignInException(string code, string logDetail = "") : Exception(code)
{
    public EntraSignInException()
        : this("entra_error")
    {
    }

    public EntraSignInException(string code, Exception innerException)
        : this(code, innerException?.GetType().Name ?? "")
    {
    }

    public string Code { get; } = code;

    public string LogDetail { get; } = logDetail;
}

/// <summary>What the sign-in round trip remembers in the short-lived signed cookie.</summary>
public sealed record EntraFlow(string State, string Nonce, string Verifier, string Next);

/// <summary>The claims the app uses from a validated ID token.</summary>
public sealed record EntraClaims(string Oid, string? Email, string? PreferredUsername);

/// <summary>
/// Microsoft Entra ID sign-in (OpenID Connect, authorization code with PKCE; <c>app/services/entra.py</c>). The
/// cryptography and network calls are implemented in Infrastructure.
/// </summary>
public interface IEntraSignIn
{
    const string FlowCookie = "ats_oidc";
    const string FlowCookiePath = "/api/auth/entra";
    const int FlowTtlSeconds = 600;

    (string Verifier, string Challenge) NewPkce();

    string AuthorizeUrl(string state, string nonce, string codeChallenge);

    /// <summary>The flow as an HMAC-signed cookie value that expires after <see cref="FlowTtlSeconds"/>.</summary>
    string DumpFlow(EntraFlow flow);

    /// <summary>The flow, or null when the cookie is missing, tampered with or expired.</summary>
    EntraFlow? LoadFlow(string? cookie);

    /// <summary>Exchanges the code (with the PKCE verifier) and validates the ID token, or throws <see cref="EntraSignInException"/>.</summary>
    Task<EntraClaims> SignInAsync(string code, string verifier, string nonce, CancellationToken ct = default);
}

public static class EntraAccounts
{
    /// <summary>Only same-site relative paths, so the sign-in cannot be used as an open redirect.</summary>
    public static string SafeNext(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
        {
            return "/";
        }

        return path;
    }

    /// <summary>
    /// The active account for these claims: by Entra object id, or the first time by email (then the object id is
    /// linked and audited). Nobody is created automatically: HR adds people, Entra only proves who they are.
    /// </summary>
    public static async Task<User> FindUserAsync(IAtsDb db, EntraClaims claims, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(claims);
        var oid = claims.Oid.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower() (Python: func.lower), not run in .NET
        var user = await AuthService.WithProfile(db.Users)
            .FirstOrDefaultAsync(u => u.GraphUserId != null && u.GraphUserId.ToLower() == oid, ct).ConfigureAwait(false);
#pragma warning restore CA1304, CA1311, CA1862
        if (user is null)
        {
            var email = (claims.Email ?? claims.PreferredUsername ?? "").Trim().ToLowerInvariant();
            if (email.Length > 0)
            {
                user = await AuthService.WithProfile(db.Users).FirstOrDefaultAsync(u => u.Email == email, ct).ConfigureAwait(false);
            }

            if (user is null)
            {
                throw new EntraSignInException("no_account", "no ATS account for this Microsoft account");
            }

            if (user.GraphUserId is not null && !string.Equals(user.GraphUserId, oid, StringComparison.OrdinalIgnoreCase))
            {
                // Already linked to a different Microsoft identity: never re-link silently.
                throw new EntraSignInException("account_mismatch", $"user {user.Id} linked to another object id");
            }

            user.GraphUserId = oid;
            AuditTrail.Record(db, user.Id, "USER_IDENTITY_LINKED", "user", user.Id,
                              after: new Dictionary<string, object?> { ["graph_user_id"] = oid, ["via"] = "entra_sign_in" }, ctx: ctx);
        }

        if (!user.IsActive)
        {
            throw new EntraSignInException("account_inactive", $"user {user.Id} inactive");
        }

        return user;
    }
}
