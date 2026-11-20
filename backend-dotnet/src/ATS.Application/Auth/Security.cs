using System.Security.Cryptography;
using System.Text;
using Ats.Application.Common;
using Ats.Application.Configuration;

namespace Ats.Application.Auth;

/// <summary>Argon2id password hashes in PHC form, compatible with argon2-cffi (implemented in Infrastructure).</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>False for a wrong password, a missing hash (still spending the time) or an unreadable hash.</summary>
    bool Verify(string? passwordHash, string password);

    /// <summary>The hash was made with other parameters than the current ones.</summary>
    bool NeedsRehash(string passwordHash);
}

/// <summary>Session tokens (<c>app/core/security.py</c>): random, and stored only as a keyed hash.</summary>
public static class Tokens
{
    /// <summary><c>secrets.token_urlsafe(32)</c>.</summary>
    public static string Generate() => SettingsLoader.TokenUrlSafe(32);

    /// <summary>HMAC-SHA256 of the token with SECRET_KEY, lower-case hex: a database leak does not reveal live cookies.</summary>
    public static string Hash(string token, string secretKey)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(secretKey);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>Constant-time comparison; false when either is missing.</summary>
    public static bool Equal(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Password rules (<c>validate_password_strength</c>).</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    public static void Validate(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinLength)
        {
            throw new ValidationFailedException($"Password must be at least {MinLength} characters long");
        }

        if (password.Length > 128)
        {
            throw new ValidationFailedException("Password must be at most 128 characters long");
        }

        if (!password.Any(char.IsAsciiLetter) || !password.Any(char.IsDigit)) // Python: [A-Za-z] and \d (any Unicode digit)
        {
            throw new ValidationFailedException("Password must contain both letters and digits");
        }
    }
}
