using System.Security.Claims;
using System.Text.Encodings.Web;
using Ats.Api.Middleware;
using Ats.Application.Auth;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Domain;
using Ats.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ats.Api.Auth;

/// <summary>Names of the authentication scheme and the role policies.</summary>
public static class SessionAuth
{
    public const string Scheme = "ats-session";

    /// <summary>Any signed-in, active user (Python: CurrentUser).</summary>
    public const string User = "User";

    /// <summary>MANAGER or ADMIN (Python: ManagerOrAdmin).</summary>
    public const string ManagerOrAdmin = "ManagerOrAdmin";

    /// <summary>ADMIN, shown as "HR admin" (Python: AdminUser).</summary>
    public const string Admin = "Admin";

    public static IServiceCollection AddSessionAuth(this IServiceCollection services)
    {
        // No default scheme: only endpoints with [Authorize] authenticate, as only Python routes with the auth
        // dependency do. Public endpoints (login, health) never read the session or check CSRF.
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(Scheme, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(User, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser())
            .AddPolicy(ManagerOrAdmin, p => p.AddAuthenticationSchemes(Scheme).RequireRole(EnumText.Of(Role.Manager), EnumText.Of(Role.Admin)))
            .AddPolicy(Admin, p => p.AddAuthenticationSchemes(Scheme).RequireRole(EnumText.Of(Role.Admin)))
            .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(Scheme).RequireAuthenticatedUser().Build());
        return services;
    }
}

/// <summary>The signed-in user and their session, for the current request.</summary>
public sealed record AuthState(User User, UserSession Session)
{
    internal const string ItemKey = "ats.auth";
}

public static class AuthStateExtensions
{
    /// <summary>The authenticated user of an [Authorize] endpoint.</summary>
    public static AuthState Auth(this HttpContext context) =>
        context.Items.TryGetValue(AuthState.ItemKey, out var state) && state is AuthState auth
            ? auth
            : throw new InvalidOperationException("No authenticated user: the endpoint needs [Authorize]");

    /// <summary>Request id, client IP (X-Forwarded-For only when TRUST_PROXY_HEADERS) and user agent, for the audit log.</summary>
    public static RequestContext ToRequestContext(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.RequestServices.GetRequiredService<AtsSettings>();
        string? ip = null;
        if (settings.TrustProxyHeaders && context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            var first = forwarded.Split(',')[0].Trim();
            ip = first[..Math.Min(64, first.Length)];
        }

        ip ??= context.Connection.RemoteIpAddress?.ToString();
        var ua = context.Request.Headers.UserAgent.ToString();
        return new RequestContext(context.GetRequestId(), ip, ua.Length > 0 ? ua : null);
    }
}

/// <summary>
/// Authenticates the <c>ats_session</c> cookie against the server-side session (<c>get_auth</c> in Python) and, for
/// state-changing methods, checks that the CSRF header echoes the session's token (double submit). Failures are
/// raised with the Python codes and status: 401 <c>not_authenticated</c> / <c>session_invalid</c> /
/// <c>session_expired</c> / <c>account_inactive</c>, 403 <c>csrf_failed</c>; a missing role is 403 <c>forbidden</c>.
/// The error middleware renders them in the usual envelope.
/// </summary>
internal sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
                                                   ILoggerFactory logger, UrlEncoder encoder, AuthService auth,
                                                   AtsSettings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string FailureKey = "ats.auth.failure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            var (user, session) = await auth.AuthenticateAsync(Request.Cookies[settings.SessionCookieName], Context.RequestAborted)
                .ConfigureAwait(false);
            if (!(HttpMethods.IsGet(Request.Method) || HttpMethods.IsHead(Request.Method) || HttpMethods.IsOptions(Request.Method))
                && !Tokens.Equal(Request.Headers[settings.CsrfHeaderName].ToString(), session.CsrfToken))
            {
                throw new ForbiddenException("Missing or invalid CSRF token", "csrf_failed");
            }

            Context.Items[AuthState.ItemKey] = new AuthState(user, session);
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Role, EnumText.Of(user.Role)),
            ], Scheme.Name);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
        }
        catch (AppException ex)
        {
            Context.Items[FailureKey] = ex;
            return AuthenticateResult.Fail(ex.Message);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        throw (Context.Items.TryGetValue(FailureKey, out var failure) && failure is AppException ex
            ? ex
            : new UnauthorizedException("Not signed in", "not_authenticated"));

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        throw new ForbiddenException("You do not have permission to perform this action");
}
