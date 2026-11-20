using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ats.Application.Auth;
using Ats.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ats.Infrastructure.Security;

/// <summary>Microsoft's token endpoint and signing keys (replaced in tests, like Python's _post_token / _signing_key).</summary>
public interface IEntraHttp
{
    /// <summary>The token endpoint's JSON response; throws <see cref="EntraSignInException"/> on HTTP or network errors.</summary>
    Task<JsonObject> PostTokenAsync(string tokenUrl, IReadOnlyDictionary<string, string> form, CancellationToken ct);

    Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(string jwksUri, CancellationToken ct);
}

public sealed class EntraHttp(HttpClient http) : IEntraHttp
{
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim KeyLock = new(1, 1);
    private static (string Uri, DateTimeOffset Fetched, IReadOnlyList<SecurityKey> Keys)? _keys;

    public async Task<JsonObject> PostTokenAsync(string tokenUrl, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(new Uri(tokenUrl), content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new EntraSignInException("entra_unreachable", ex.GetType().Name);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonObject? body = null;
            try
            {
                body = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                // not JSON: reported below
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EntraSignInException("token_exchange_failed",
                                               $"HTTP {(int)response.StatusCode} {body?["error"]?.GetValue<string>() ?? ""}");
            }

            return body ?? throw new EntraSignInException("token_exchange_failed", "response is not JSON");
        }
    }

    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(string jwksUri, CancellationToken ct)
    {
        await KeyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_keys is { } cached && cached.Uri == jwksUri && DateTimeOffset.UtcNow - cached.Fetched < KeyLifetime)
            {
                return cached.Keys;
            }

            var json = await http.GetStringAsync(new Uri(jwksUri), ct).ConfigureAwait(false);
            var keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
            _keys = (jwksUri, DateTimeOffset.UtcNow, keys);
            return keys;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException)
        {
            throw new EntraSignInException("invalid_token", $"signing key: {ex.GetType().Name}");
        }
        finally
        {
            KeyLock.Release();
        }
    }
}

/// <summary>
/// The Entra ID flow of <c>app/services/entra.py</c>: PKCE (S256), an HMAC-signed flow cookie, the authorization
/// URL, the code exchange, and ID-token validation (RS256 with the tenant's keys, issuer, audience, expiry with 60 s
/// leeway, required claims, nonce, tenant id, object id). Times are real time (as PyJWT and time.time()).
/// </summary>
public sealed partial class EntraSignIn(AtsSettings settings, IEntraHttp http, ILogger<EntraSignIn> logger) : IEntraSignIn
{
    private const string Scopes = "openid profile email";
    private static readonly string[] RequiredClaims = ["exp", "iat", "aud", "iss", "sub"];

    private string TenantBase => $"{settings.EntraAuthority.TrimEnd('/')}/{settings.EntraTenantId}";

    public string Issuer => $"{TenantBase}/v2.0";

    public (string Verifier, string Challenge) NewPkce()
    {
        var verifier = SettingsLoader.TokenUrlSafe(64);
        return (verifier, B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    /// <summary>Query parameters in Python's order, encoded like urllib's urlencode (spaces as '+').</summary>
    public string AuthorizeUrl(string state, string nonce, string codeChallenge)
    {
        (string Key, string Value)[] query =
        [
            ("client_id", settings.EntraClientId), ("response_type", "code"), ("redirect_uri", settings.EntraRedirectUri),
            ("response_mode", "query"), ("scope", Scopes), ("state", state), ("nonce", nonce),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256"),
        ];
        return $"{TenantBase}/oauth2/v2.0/authorize?" +
               string.Join("&", query.Select(q => $"{WebUtility.UrlEncode(q.Key)}={WebUtility.UrlEncode(q.Value)}"));
    }

    public string DumpFlow(EntraFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["state"] = flow.State, ["nonce"] = flow.Nonce, ["verifier"] = flow.Verifier, ["next"] = flow.Next,
            ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + IEntraSignIn.FlowTtlSeconds,
        });
        var payload = B64(Encoding.UTF8.GetBytes(json));
        return $"{payload}.{Mac(payload)}";
    }

    public EntraFlow? LoadFlow(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie) || !cookie.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var dot = cookie.LastIndexOf('.');
        var (payload, mac) = (cookie[..dot], cookie[(dot + 1)..]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(Mac(payload))))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(Unb64(payload));
            var root = doc.RootElement;
            if (root.GetProperty("exp").GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return null;
            }

            return new EntraFlow(root.GetProperty("state").GetString()!, root.GetProperty("nonce").GetString()!,
                                 root.GetProperty("verifier").GetString()!, root.GetProperty("next").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<EntraClaims> SignInAsync(string code, string verifier, string nonce, CancellationToken ct = default)
    {
        var tokens = await http.PostTokenAsync($"{TenantBase}/oauth2/v2.0/token", new Dictionary<string, string>
        {
            ["client_id"] = settings.EntraClientId, ["client_secret"] = settings.EntraClientSecret,
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = settings.EntraRedirectUri,
            ["code_verifier"] = verifier, ["scope"] = Scopes,
        }, ct).ConfigureAwait(false);
        var idToken = tokens["id_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(idToken))
        {
            throw new EntraSignInException("token_exchange_failed", "no id_token in response");
        }

        return await ValidateAsync(idToken, nonce, ct).ConfigureAwait(false);
    }

    private async Task<EntraClaims> ValidateAsync(string idToken, string nonce, CancellationToken ct)
    {
        var keys = await http.GetSigningKeysAsync($"{TenantBase}/discovery/v2.0/keys", ct).ConfigureAwait(false);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = settings.EntraClientId,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(60),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
        }).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new EntraSignInException("invalid_token", result.Exception?.GetType().Name ?? "invalid");
        }

        var claims = result.Claims;
        if (RequiredClaims.FirstOrDefault(c => !claims.ContainsKey(c)) is { } missing)
        {
            throw new EntraSignInException("invalid_token", $"MissingRequiredClaimError {missing}");
        }

        var tokenNonce = claims.TryGetValue("nonce", out var n) ? Convert.ToString(n, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tokenNonce), Encoding.UTF8.GetBytes(nonce)))
        {
            throw new EntraSignInException("invalid_token", "nonce mismatch");
        }

        if (!claims.TryGetValue("tid", out var tid) || (tid as string) != settings.EntraTenantId)
        {
            throw new EntraSignInException("invalid_token", "tenant mismatch");
        }

        if (!claims.TryGetValue("oid", out var oid) || oid is not string oidText || oidText.Length == 0)
        {
            throw new EntraSignInException("invalid_token", "no oid claim");
        }

        LogValidated(logger, oidText.Length);
        return new EntraClaims(oidText, claims.TryGetValue("email", out var email) ? email as string : null,
                               claims.TryGetValue("preferred_username", out var upn) ? upn as string : null);
    }

    private string Mac(string payload)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("entra-flow:" + settings.SecretKey));
        return B64(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload)));
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Unb64(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - (s.Length % 4)) % 4));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entra ID token validated (oid length {Length})")]
    private static partial void LogValidated(ILogger logger, int length);
}
