using System.Security.Cryptography;
using System.Text;
using Ats.Api.Auth;
using Ats.Application.Auth;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ats.Api.Controllers;

/// <summary>Sign-in, sessions and the current user (<c>backend/app/api/routes/auth.py</c>).</summary>
[ApiController]
[Route("api/auth")]
[Tags("auth")]
public sealed partial class AuthController(AuthService auth, AtsSettings settings, IAtsDb db, ILogger<AuthController> logger)
    : ControllerBase
{
    /// <summary>Sign-in methods the login page should offer.</summary>
    [HttpGet("providers")]
    public ProvidersOut Providers() =>
        new(settings.PasswordLoginEnabled, settings.EntraEnabled, settings.DevLoginEnabled && !settings.IsProduction);

    /// <summary>Sign in with email and password.</summary>
    [HttpPost("login")]
    public async Task<MeResponse> Login([FromBody] LoginRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!settings.PasswordLoginEnabled)
        {
            throw new ForbiddenException("Password sign-in is turned off. Sign in with Microsoft.", "password_login_disabled");
        }

        var result = await auth.LoginAsync(body.Email!, body.Password!, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        SetCookies(result.Token, result.Session.CsrfToken);
        return await auth.MeAsync(result.User, result.Session.CsrfToken, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Local testing only: sign in as an active user without a password. Off unless DEV_LOGIN_ENABLED, and impossible
    /// in production (settings refuse to start). Every use is audited as method "dev".
    /// </summary>
    [HttpPost("dev-login")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<MeResponse> DevLogin([FromBody] DevLoginRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!settings.DevLoginEnabled || settings.IsProduction)
        {
            throw new NotFoundException("Not found");
        }

        var who = string.Join(' ', body.Email!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var users = AuthService.WithProfile(db.Users);
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL upper() (Python: func.upper), not run in .NET
        var user = who.Contains('@', StringComparison.Ordinal)
            ? await users.FirstOrDefaultAsync(u => u.Email == who.ToLowerInvariant(), ct).ConfigureAwait(false)
            // an employee ID, matched like timesheet imports: case-insensitive, runs of spaces as one
            : await users.FirstOrDefaultAsync(u => u.EmployeeCode.ToUpper() == who.ToUpperInvariant(), ct).ConfigureAwait(false);
#pragma warning restore CA1304, CA1311, CA1862
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("No active account with that email or employee ID", "invalid_credentials");
        }

        var result = await auth.StartSessionAsync(user, HttpContext.ToRequestContext(), "dev", ct).ConfigureAwait(false);
        SetCookies(result.Token, result.Session.CsrfToken);
        return await auth.MeAsync(result.User, result.Session.CsrfToken, ct).ConfigureAwait(false);
    }

    /// <summary>Start Microsoft Entra ID sign-in (redirects to Microsoft).</summary>
    [HttpGet("entra/login")]
    public IActionResult EntraLogin([FromServices] IEntraSignIn entra, [FromQuery(Name = "next")] string? next = null)
    {
        ArgumentNullException.ThrowIfNull(entra);
        if (!settings.EntraEnabled)
        {
            throw new NotFoundException("Microsoft sign-in is not configured");
        }

        if (next is { Length: > 500 })
        {
            const string tooLong = "String should have at most 500 characters";
            throw new ValidationFailedException($"query.next: {tooLong}", details: new[] { new Middleware.ValidationErrors.Detail("query.next", tooLong) });
        }

        var (state, nonce) = (SettingsLoader.TokenUrlSafe(32), SettingsLoader.TokenUrlSafe(32));
        var (verifier, challenge) = entra.NewPkce();
        // SameSite=Lax: sent on Microsoft's top-level redirect back to the callback.
        Response.Cookies.Append(IEntraSignIn.FlowCookie, entra.DumpFlow(new EntraFlow(state, nonce, verifier, EntraAccounts.SafeNext(next))),
            new CookieOptions
            {
                MaxAge = TimeSpan.FromSeconds(IEntraSignIn.FlowTtlSeconds), Path = IEntraSignIn.FlowCookiePath, HttpOnly = true,
                Secure = settings.SecureCookies, SameSite = SameSiteMode.Lax,
            });
        return Redirect302(entra.AuthorizeUrl(state, nonce, challenge));
    }

    /// <summary>Microsoft redirects back here with the code; on success the session cookies are set.</summary>
    [HttpGet("entra/callback")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> EntraCallback([FromServices] IEntraSignIn entra, [FromQuery] string? code = null,
                                                   [FromQuery] string? state = null, [FromQuery] string? error = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entra);
        if (!settings.EntraEnabled)
        {
            throw new NotFoundException("Microsoft sign-in is not configured");
        }

        var flow = entra.LoadFlow(Request.Cookies[IEntraSignIn.FlowCookie]);
        IActionResult response;
        try
        {
            if (!string.IsNullOrEmpty(error))
            {
                throw new EntraSignInException(error == "access_denied" ? "entra_denied" : "entra_error", error[..Math.Min(64, error.Length)]);
            }

            if (flow is null)
            {
                throw new EntraSignInException("sign_in_expired", "flow cookie missing, invalid or expired");
            }

            if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code)
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(flow.State)))
            {
                throw new EntraSignInException("sign_in_expired", "state mismatch or no code");
            }

            var claims = await entra.SignInAsync(code, flow.Verifier, flow.Nonce, ct).ConfigureAwait(false);
            var user = await EntraAccounts.FindUserAsync(db, claims, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
            var result = await auth.StartSessionAsync(user, HttpContext.ToRequestContext(), "entra", ct).ConfigureAwait(false);
            response = Redirect302(flow.Next);
            SetCookies(result.Token, result.Session.CsrfToken);
        }
        catch (EntraSignInException ex)
        {
            db.ChangeTracker.Clear(); // nothing of a refused sign-in is saved (Python: db.rollback())
            LogRefused(logger, ex.Code, ex.LogDetail);
            response = Redirect302($"/login?error={ex.Code}");
        }

        Response.Cookies.Delete(IEntraSignIn.FlowCookie, new CookieOptions
        {
            Path = IEntraSignIn.FlowCookiePath, Secure = settings.SecureCookies, SameSite = SameSiteMode.Lax,
        });
        return response;
    }

    [HttpPost("logout")]
    [Authorize(Policy = SessionAuth.User)]
    public async Task<MessageResponse> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(HttpContext.Auth().Session, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        foreach (var name in new[] { settings.SessionCookieName, settings.CsrfCookieName })
        {
            Response.Cookies.Delete(name, CookieOptions(httpOnly: false, maxAge: null));
        }

        return new MessageResponse("Signed out");
    }

    /// <summary>Current user profile and CSRF token.</summary>
    [HttpGet("me")]
    [Authorize(Policy = SessionAuth.User)]
    public Task<MeResponse> Me(CancellationToken ct)
    {
        var state = HttpContext.Auth();
        return auth.MeAsync(state.User, state.Session.CsrfToken, ct);
    }

    [HttpPost("change-password")]
    [Authorize(Policy = SessionAuth.User)]
    public async Task<MessageResponse> ChangePassword([FromBody] ChangePasswordRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var state = HttpContext.Auth();
        await auth.ChangePasswordAsync(state.User, state.Session, body.CurrentPassword!, body.NewPassword!,
                                       HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        return new MessageResponse("Password changed. Other sessions have been signed out.");
    }

    private void SetCookies(string token, string csrf)
    {
        var maxAge = TimeSpan.FromHours(settings.SessionAbsoluteHours);
        Response.Cookies.Append(settings.SessionCookieName, token, CookieOptions(httpOnly: true, maxAge));
        // Readable by the SPA so it can echo the value in the X-CSRF-Token header.
        Response.Cookies.Append(settings.CsrfCookieName, csrf, CookieOptions(httpOnly: false, maxAge));
    }

    private CookieOptions CookieOptions(bool httpOnly, TimeSpan? maxAge) => new()
    {
        HttpOnly = httpOnly, MaxAge = maxAge, Secure = settings.SecureCookies, Domain = settings.CookieDomain, Path = "/",
        SameSite = settings.CookieSameSite switch
        {
            "strict" => SameSiteMode.Strict,
            "none" => SameSiteMode.None,
            _ => SameSiteMode.Lax,
        },
    };

    /// <summary>Starlette's RedirectResponse(status_code=302).</summary>
    private static RedirectResult Redirect302(string url) => new(url, permanent: false, preserveMethod: false);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entra sign-in refused: {Code} ({Detail})")]
    private static partial void LogRefused(ILogger logger, string code, string detail);
}
