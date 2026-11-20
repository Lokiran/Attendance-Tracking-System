using System.Globalization;
using System.Text.RegularExpressions;
using FluentValidation;

namespace Ats.Application.Validation;

/// <summary>
/// Validation rules with Pydantic's messages, so 422 responses read like the Python API's
/// (<c>"Field required"</c>, <c>"String should have at least 1 character"</c>, ...).
/// </summary>
public static partial class PyValidation
{
    public const string FieldRequired = "Field required";

    public static IRuleBuilderOptions<T, string?> RequiredString<T>(this IRuleBuilderInitial<T, string?> rule, int minLength, int maxLength) =>
        rule.Cascade(CascadeMode.Stop)
            .NotNull().WithMessage(FieldRequired)
            .Must(v => v!.Length >= minLength).WithMessage(MinMessage(minLength))
            .Must(v => v!.Length <= maxLength).WithMessage(MaxMessage(maxLength));

    public static IRuleBuilderOptions<T, string?> OptionalString<T>(this IRuleBuilder<T, string?> rule, int minLength, int maxLength) =>
        rule.Must(v => v is null || v.Length >= minLength).WithMessage(MinMessage(minLength))
            .Must(v => v is null || v.Length <= maxLength).WithMessage(MaxMessage(maxLength));

    /// <summary>Pydantic's <c>EmailStr</c> (email-validator 2.x without deliverability checks).</summary>
    public static IRuleBuilderOptions<T, string?> PyEmail<T>(this IRuleBuilderInitial<T, string?> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotNull().WithMessage(FieldRequired)
            .Must(v => EmailProblem(v!) is null)
            .WithMessage((_, value) => $"value is not a valid email address: {EmailProblem(value!)}");

    public static string MinMessage(int n) =>
        $"String should have at least {n.ToString(CultureInfo.InvariantCulture)} character{(n == 1 ? "" : "s")}";

    public static string MaxMessage(int n) =>
        $"String should have at most {n.ToString(CultureInfo.InvariantCulture)} character{(n == 1 ? "" : "s")}";

    private static readonly string[] SpecialUseDomains = ["arpa", "invalid", "local", "localhost", "onion", "test"];

    /// <summary>Why email-validator would refuse the address (its wording), or null when it is valid.</summary>
    public static string? EmailProblem(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var at = email.LastIndexOf('@');
        if (at < 0)
        {
            return "An email address must have an @-sign.";
        }

        var local = email[..at];
        var domain = email[(at + 1)..];
        if (local.Length == 0)
        {
            return "There must be something before the @-sign.";
        }

        if (domain.Length == 0)
        {
            return "There must be something after the @-sign.";
        }

        if (local.Length > 64)
        {
            return "The email address is too long before the @-sign.";
        }

        if (email.Contains("..", StringComparison.Ordinal))
        {
            return "An email address cannot have two periods in a row.";
        }

        if (local.StartsWith('.'))
        {
            return "An email address cannot start with a period.";
        }

        if (local.EndsWith('.'))
        {
            return "An email address cannot have a period immediately before the @-sign.";
        }

        var badLocal = local.Where(c => !(char.IsLetterOrDigit(c) || "!#$%&'*+-/=?^_`{|}~.".Contains(c) || c > 0x7f))
            .Distinct().ToList();
        if (badLocal.Count > 0)
        {
            return $"The email address contains invalid characters before the @-sign: {string.Join(", ", badLocal.Select(c => $"'{c}'"))}.";
        }

        if (domain.StartsWith('.'))
        {
            return "An email address cannot have a period immediately after the @-sign.";
        }

        if (domain.EndsWith('.'))
        {
            return "An email address cannot end with a period.";
        }

        if (!domain.Contains('.', StringComparison.Ordinal))
        {
            return "The part after the @-sign is not valid. It should have a period.";
        }

        var labels = domain.Split('.');
        if (labels.Any(l => l.StartsWith('-')))
        {
            return "An email address cannot have a hyphen immediately after the @-sign.";
        }

        if (labels.Any(l => l.EndsWith('-')))
        {
            return "An email address cannot have a hyphen immediately before the @-sign.";
        }

        if (labels.Any(l => !DomainLabel().IsMatch(l)))
        {
            return "The part after the @-sign contains invalid characters.";
        }

        var lower = domain.ToLowerInvariant();
        if (SpecialUseDomains.Any(s => lower == s || lower.EndsWith("." + s, StringComparison.Ordinal)))
        {
            return "The part after the @-sign is a special-use or reserved name that cannot be used with email.";
        }

        if (labels[^1].All(char.IsAsciiDigit))
        {
            return "The part after the @-sign is not valid. It is not within a valid top-level domain.";
        }

        return null;
    }

    [GeneratedRegex(@"^[\p{L}\p{N}-]{1,63}$")]
    private static partial Regex DomainLabel();
}
