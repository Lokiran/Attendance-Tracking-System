using System.Globalization;
using System.Text.RegularExpressions;
using Ats.Application.Auth;
using Isopoh.Cryptography.Argon2;

namespace Ats.Infrastructure.Security;

/// <summary>
/// Argon2id with argon2-cffi's <c>PasswordHasher()</c> defaults (RFC 9106 low-memory profile): t=3, m=65536 KiB,
/// p=4, 32-byte hash, 16-byte salt, PHC string <c>$argon2id$v=19$m=65536,t=3,p=4$salt$hash</c>. Hashes written by
/// the Python backend verify unchanged, and hashes written here verify in Python.
/// </summary>
public sealed partial class Argon2PasswordHasher : IPasswordHasher
{
    private const int TimeCost = 3;
    private const int MemoryCost = 65536;
    private const int Parallelism = 4;
    private const int HashLength = 32;
    private const int SaltLength = 16;

    /// <summary>Verified against when the account does not exist, so response time does not reveal it.</summary>
    private static readonly Lazy<string> DummyHash = new(() => new Argon2PasswordHasher().Hash(Tokens.Generate()));

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Argon2.Hash(password, TimeCost, MemoryCost, Parallelism, Argon2Type.HybridAddressing, HashLength);
    }

    public bool Verify(string? passwordHash, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        try
        {
            return Argon2.Verify(passwordHash ?? DummyHash.Value, password) && passwordHash is not null;
        }
#pragma warning disable CA1031 // an unreadable hash is a failed verification (argon2-cffi: InvalidHashError -> False)
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>argon2-cffi's <c>check_needs_rehash</c>: type, version, memory, time, parallelism, salt and hash length.</summary>
    public bool NeedsRehash(string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);
        var m = Phc().Match(passwordHash);
        if (!m.Success)
        {
            return true;
        }

        static int Int(Match m, string g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return m.Groups["type"].Value != "argon2id" || Int(m, "v") != 19 || Int(m, "m") != MemoryCost
               || Int(m, "t") != TimeCost || Int(m, "p") != Parallelism
               || DecodedLength(m.Groups["salt"].Value) != SaltLength || DecodedLength(m.Groups["hash"].Value) != HashLength;
    }

    private static int DecodedLength(string base64NoPadding) => base64NoPadding.Length * 3 / 4;

    [GeneratedRegex(@"^\$(?<type>argon2(?:id|i|d))\$v=(?<v>\d+)\$m=(?<m>\d+),t=(?<t>\d+),p=(?<p>\d+)\$(?<salt>[A-Za-z0-9+/]+)\$(?<hash>[A-Za-z0-9+/]+)$")]
    private static partial Regex Phc();
}
