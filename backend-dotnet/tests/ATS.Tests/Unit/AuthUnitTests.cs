using System.Text.Json.Nodes;
using Ats.Application.Audit;
using Ats.Application.Auth;
using Ats.Application.Common;
using Ats.Application.Python;
using Ats.Application.Validation;
using Ats.Domain;
using Ats.Infrastructure.Security;
using Ats.Tests.Auth;
using FluentAssertions;

namespace Ats.Tests.Unit;

/// <summary>Helpers checked against values produced by the Python backend.</summary>
public sealed class AuthUnitTests
{
    [Fact]
    public void Session_token_hashes_match_python_hmac() =>
        // hmac.new(b"test-secret-key", b"session-token-value", hashlib.sha256).hexdigest()
        Tokens.Hash("session-token-value", "test-secret-key").Should().Be("f069475d7c436e29821b4b15e0aeadd0128d306ff0443e99a4bc51c653d8d525");

    [Fact]
    public void Tokens_are_url_safe_and_compared_in_constant_time()
    {
        var t = Tokens.Generate();
        t.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
        Tokens.Equal(t, t).Should().BeTrue();
        Tokens.Equal(t, t + "x").Should().BeFalse();
        Tokens.Equal(null, t).Should().BeFalse();
        Tokens.Equal("", "").Should().BeFalse();
    }

    [Fact]
    public void Argon2_is_compatible_with_argon2_cffi()
    {
        var hasher = new Argon2PasswordHasher();
        hasher.Verify(AuthHost.PythonHash, AuthHost.Password).Should().BeTrue();
        hasher.Verify(AuthHost.PythonHash, "wrong").Should().BeFalse();
        hasher.Verify(null, AuthHost.Password).Should().BeFalse();
        hasher.Verify("not-a-hash", AuthHost.Password).Should().BeFalse();
        hasher.NeedsRehash(AuthHost.PythonHash).Should().BeFalse();
        hasher.NeedsRehash("$argon2id$v=19$m=65536,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g").Should().BeTrue();

        var mine = hasher.Hash("An0therStrongOne");
        mine.Should().MatchRegex(@"^\$argon2id\$v=19\$m=65536,t=3,p=4\$[A-Za-z0-9+/]{22}\$[A-Za-z0-9+/]{43}$");
        hasher.Verify(mine, "An0therStrongOne").Should().BeTrue();
        hasher.NeedsRehash(mine).Should().BeFalse();
    }

    [Theory]
    [InlineData("short", "Password must be at least 10 characters long")]
    [InlineData("onlyletters", "Password must contain both letters and digits")]
    [InlineData("1234567890", "Password must contain both letters and digits")]
    public void Password_policy_messages(string password, string message) =>
        ((Action)(() => PasswordPolicy.Validate(password))).Should().Throw<ValidationFailedException>().WithMessage(message);

    [Fact]
    public void Password_policy_accepts_unicode_digits_like_python()
    {
        PasswordPolicy.Validate("Abcdefghij٣"); // Arabic-Indic three matches Python's \d
        ((Action)(() => PasswordPolicy.Validate(new string('a', 120) + "123456789"))).Should().Throw<ValidationFailedException>()
            .WithMessage("Password must be at most 128 characters long");
    }

    [Theory]
    [InlineData("Admin@Nexa.Example", null)]
    [InlineData("a@b.c", null)]
    [InlineData("bad", "An email address must have an @-sign.")]
    [InlineData("a@b", "The part after the @-sign is not valid. It should have a period.")]
    [InlineData("x@localhost", "The part after the @-sign is not valid. It should have a period.")]
    [InlineData("a@test.test", "The part after the @-sign is a special-use or reserved name that cannot be used with email.")]
    [InlineData("a..b@x.com", "An email address cannot have two periods in a row.")]
    [InlineData("@x.com", "There must be something before the @-sign.")]
    public void Email_validation_matches_email_validator(string email, string? problem) =>
        PyValidation.EmailProblem(email).Should().Be(problem);

    [Fact]
    public void Python_repr_and_isoformat()
    {
        PyFormat.Repr("10001").Should().Be("'10001'");
        PyFormat.Repr("O'Brien").Should().Be("\"O'Brien\"");
        PyFormat.Repr("a'b\"c").Should().Be("'a\\'b\"c'");
        PyFormat.Repr("tab\there").Should().Be("'tab\\there'");
        PyFormat.IsoDateTime(new DateTimeOffset(2026, 11, 19, 8, 39, 37, TimeSpan.Zero).AddTicks(5774360)).Should().Be("2026-11-19T08:39:37.577436+00:00");
        PyFormat.IsoDateTime(new DateTimeOffset(2026, 11, 19, 14, 9, 0, TimeSpan.FromHours(5.5))).Should().Be("2026-11-19T08:39:00+00:00");
        PyFormat.IsoDate(new DateOnly(2026, 9, 1)).Should().Be("2026-09-01");
    }

    [Fact]
    public void Audit_values_drop_secrets_and_store_enums_and_dates_as_text()
    {
        var clean = (JsonObject)AuditTrail.Clean(new Dictionary<string, object?>
        {
            ["full_name"] = "Rahul", ["password"] = "x", ["password_hash"] = "y", ["token"] = "t", ["csrf_token"] = "c",
            ["secret"] = "s", ["role"] = Role.Manager, ["date_joined"] = new DateOnly(2025, 1, 1),
            ["nested"] = new Dictionary<string, object?> { ["token_hash"] = "h", ["ok"] = true },
            ["days"] = new List<int> { 1, 2 }, ["none"] = null,
        })!;
        clean.ToJsonString().Should().Be("""{"full_name":"Rahul","role":"MANAGER","date_joined":"2025-01-01","nested":{"ok":true},"days":[1,2],"none":null}""");
    }

    [Fact]
    public void Audit_changes_keep_only_changed_keys()
    {
        var (before, after) = AuditTrail.Changes(
            new Dictionary<string, object?> { ["a"] = 1, ["b"] = "x", ["c"] = null },
            new Dictionary<string, object?> { ["a"] = 1, ["b"] = "y", ["c"] = 5 });
        before.Should().Equal(new Dictionary<string, object?> { ["b"] = "x", ["c"] = null });
        after.Should().Equal(new Dictionary<string, object?> { ["b"] = "y", ["c"] = 5 });
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/team?x=1", "/team?x=1")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:x", "/")]
    public void Safe_next(string? path, string expected) => EntraAccounts.SafeNext(path).Should().Be(expected);
}
