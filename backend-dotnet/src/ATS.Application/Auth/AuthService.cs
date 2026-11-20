using Ats.Application.Audit;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Domain;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ats.Application.Auth;

public sealed record LoginResult(User User, string Token, UserSession Session);

/// <summary>Login, server-side sessions and brute-force throttling (<c>app/services/auth_service.py</c>).</summary>
public sealed partial class AuthService(IAtsDb db, AtsSettings settings, IClock clock, IPasswordHasher hasher,
                                        ILogger<AuthService> logger)
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromSeconds(60);

    private async Task CheckThrottleAsync(string email, string? ip, CancellationToken ct)
    {
        var since = clock.UtcNow.AddMinutes(-settings.LoginWindowMinutes);
        var byEmail = await db.LoginAttempts.CountAsync(a => a.Email == email && !a.Succeeded && a.CreatedAt >= since, ct)
            .ConfigureAwait(false);
        var byIp = 0;
        if (!string.IsNullOrEmpty(ip))
        {
            byIp = await db.LoginAttempts.CountAsync(a => a.IpAddress == ip && !a.Succeeded && a.CreatedAt >= since, ct)
                .ConfigureAwait(false);
        }

        if (byEmail >= settings.LoginMaxFailures || byIp >= settings.LoginIpMaxFailures)
        {
            throw new TooManyRequestsException(
                $"Too many failed sign-in attempts. Try again in {settings.LoginWindowMinutes} minutes.",
                settings.LoginWindowMinutes * 60);
        }
    }

    public async Task<LoginResult> LoginAsync(string email, string password, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(ctx);
        email = email.Trim().ToLowerInvariant();
        await CheckThrottleAsync(email, ctx.IpAddress, ct).ConfigureAwait(false);
        var user = await WithProfile(db.Users).FirstOrDefaultAsync(u => u.Email == email, ct).ConfigureAwait(false);
        var ok = hasher.Verify(user?.PasswordHash, password);
        if (!ok || user is null || !user.IsActive)
        {
            db.LoginAttempts.Add(new LoginAttempt { Email = email, IpAddress = ctx.IpAddress, Succeeded = false });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            LogFailedLogin(logger, Tokens.Hash(email, settings.SecretKey)[..12]);
            throw new UnauthorizedException("Invalid email or password", "invalid_credentials");
        }

        if (hasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = hasher.Hash(password);
        }

        db.LoginAttempts.Add(new LoginAttempt { Email = email, IpAddress = ctx.IpAddress, Succeeded = true });
        return await StartSessionAsync(user, ctx, "password", ct).ConfigureAwait(false);
    }

    /// <summary>A server-side session for an already authenticated, active user (any sign-in method).</summary>
    public async Task<LoginResult> StartSessionAsync(User user, RequestContext ctx, string method, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(ctx);
        var now = clock.UtcNow;
        var token = Tokens.Generate();
        var ua = ctx.UserAgent ?? "";
        var session = new UserSession
        {
            UserId = user.Id, TokenHash = Tokens.Hash(token, settings.SecretKey), CsrfToken = Tokens.Generate(),
            CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddHours(settings.SessionAbsoluteHours),
            IpAddress = ctx.IpAddress, UserAgent = ua.Length == 0 ? null : ua[..Math.Min(255, ua.Length)],
        };
        db.UserSessions.Add(session);
        AuditTrail.Record(db, user.Id, "LOGIN", "user", user.Id, after: new Dictionary<string, object?> { ["method"] = method }, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new LoginResult(user, token, session);
    }

    /// <summary>The signed-in user and session for a cookie token, or 401 with the Python codes.</summary>
    public async Task<(User User, UserSession Session)> AuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw new UnauthorizedException("Not signed in", "not_authenticated");
        }

        var hash = Tokens.Hash(token, settings.SecretKey);
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        if (session is null || session.RevokedAt is not null)
        {
            throw new UnauthorizedException("Your session is no longer valid. Please sign in again.", "session_invalid");
        }

        if (session.ExpiresAt <= now || session.LastSeenAt.AddMinutes(settings.SessionIdleMinutes) <= now)
        {
            throw new UnauthorizedException("Your session has expired. Please sign in again.", "session_expired");
        }

        var user = await WithProfile(db.Users).FirstOrDefaultAsync(u => u.Id == session.UserId, ct).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("Your account is inactive", "account_inactive");
        }

        if (now - session.LastSeenAt >= LastSeenResolution)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return (user, session);
    }

    public async Task LogoutAsync(UserSession session, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.RevokedAt = clock.UtcNow;
        AuditTrail.Record(db, session.UserId, "LOGOUT", "user", session.UserId, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Revokes the user's open sessions (part of the caller's transaction; the caller saves).</summary>
    public async Task RevokeAllSessionsAsync(int userId, long? exceptSessionId = null, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var open = await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && (exceptSessionId == null || s.Id != exceptSessionId))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var s in open)
        {
            s.RevokedAt = now;
        }
    }

    public async Task ChangePasswordAsync(User user, UserSession session, string current, string newPassword,
                                          RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(session);
        if (!hasher.Verify(user.PasswordHash, current))
        {
            throw new UnauthorizedException("Current password is incorrect", "invalid_credentials");
        }

        PasswordPolicy.Validate(newPassword);
        user.PasswordHash = hasher.Hash(newPassword);
        await RevokeAllSessionsAsync(user.Id, session.Id, ct).ConfigureAwait(false);
        AuditTrail.Record(db, user.Id, "PASSWORD_CHANGED", "user", user.Id, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<MeResponse> MeAsync(User user, string csrfToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var hasReports = await db.Users.AnyAsync(u => u.ManagerId == user.Id, ct).ConfigureAwait(false);
        return new MeResponse(Profile.Of(user), csrfToken, settings.OfficeTimezone, hasReports,
                              await Timesheets.LatestImportThroughAsync(db, ct).ConfigureAwait(false));
    }

    /// <summary>Users with department and manager loaded (SQLAlchemy lazy="joined").</summary>
    public static IQueryable<User> WithProfile(IQueryable<User> users) =>
        users.Include(u => u.Department).Include(u => u.Manager);

    [LoggerMessage(Level = LogLevel.Information, Message = "failed login for email hash={EmailHash}")]
    private static partial void LogFailedLogin(ILogger logger, string emailHash);
}

/// <summary>Facts about the committed timesheets (<c>attendance_data.latest_import_through</c>).</summary>
public static class Timesheets
{
    /// <summary>The last date any committed timesheet covers.</summary>
    public static async Task<DateOnly?> LatestImportThroughAsync(IAtsDb db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.AttendanceUploads
            .Where(u => u.Status == UploadStatus.Committed && u.PeriodStart != null && u.PeriodEnd != null)
            .MaxAsync(u => u.PeriodEnd, ct).ConfigureAwait(false);
    }
}
