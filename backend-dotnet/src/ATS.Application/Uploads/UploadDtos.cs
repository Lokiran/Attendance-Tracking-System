using System.Text.Json.Nodes;
using Ats.Application.Validation;
using Ats.Domain;
using Ats.Domain.Entities;
using FluentValidation;

namespace Ats.Application.Uploads;

/// <summary>A timesheet upload (<c>UploadOut</c>): the preview summary, then what the commit did.</summary>
public sealed record UploadOut(int Id, string Filename, string Status, int FileSize, int RowCount, int ErrorCount, JsonObject? Summary,
                               JsonObject? CommitSummary, bool OverwriteOverrides, string? UploadedByName, DateTimeOffset CreatedAt,
                               DateTimeOffset? CommittedAt, DateOnly? PeriodStart, DateOnly? PeriodEnd)
{
    public static UploadOut Of(AttendanceUpload u, string? uploadedByName)
    {
        ArgumentNullException.ThrowIfNull(u);
        return new(u.Id, u.Filename, EnumText.Of(u.Status), u.FileSize, u.RowCount, u.ErrorCount, u.Summary, u.CommitSummary,
                   u.OverwriteOverrides, uploadedByName, u.CreatedAt, u.CommittedAt, u.PeriodStart, u.PeriodEnd);
    }
}

public sealed class CommitRequest
{
    /// <summary>Replace days an admin overrode (they are skipped otherwise).</summary>
    public bool OverwriteOverrides { get; set; }
}

public sealed class CreateEmployeesRequest
{
    /// <summary>IDs to add (default: every unknown ID that can be added).</summary>
    public List<string>? EmployeeRefs { get; set; }
}

public sealed class OverrideRequest
{
    public string? WorkMode { get; set; }

    public string? Note { get; set; }
}

public sealed class WfoPolicyUpdate
{
    public int? MinDaysPerWeek { get; set; }

    public int? MinDaysPerMonth { get; set; }

    public int? MinOfficeMinutes { get; set; }
}

// Field constraints of backend/app/schemas/wfo.py, with Pydantic's messages. Property names are the JSON names.

public sealed class CreateEmployeesRequestValidator : AbstractValidator<CreateEmployeesRequest>
{
    public CreateEmployeesRequestValidator() =>
        RuleFor(x => x.EmployeeRefs).Must(r => r is null || r.Count <= 5000)
            .WithMessage(x => $"List should have at most 5000 items after validation, not {x.EmployeeRefs!.Count}")
            .OverridePropertyName("employee_refs");
}

public sealed class OverrideRequestValidator : AbstractValidator<OverrideRequest>
{
    public OverrideRequestValidator()
    {
        RuleFor(x => x.WorkMode).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Field required")
            .Must(m => EnumText.TryParse<WorkMode>(m, out _)).WithMessage("Input should be 'OFFICE', 'WFH', 'LEAVE', 'HOLIDAY' or 'WEEKOFF'")
            .OverridePropertyName("work_mode");
        RuleFor(x => x.Note).Must(n => n is null || n.Length <= 500).WithMessage(PyValidation.MaxMessage(500)).OverridePropertyName("note");
    }
}

public sealed class WfoPolicyUpdateValidator : AbstractValidator<WfoPolicyUpdate>
{
    public WfoPolicyUpdateValidator()
    {
        Range(x => x.MinDaysPerWeek, "min_days_per_week", 7);
        Range(x => x.MinDaysPerMonth, "min_days_per_month", 31);
        Range(x => x.MinOfficeMinutes, "min_office_minutes", 1440);
    }

    private void Range(System.Linq.Expressions.Expression<Func<WfoPolicyUpdate, int?>> field, string name, int max)
    {
        RuleFor(field).Cascade(CascadeMode.Stop)
            .Must(v => v is null or >= 0).WithMessage("Input should be greater than or equal to 0")
            .Must(v => v is null || v <= max).WithMessage($"Input should be less than or equal to {max}")
            .OverridePropertyName(name);
    }
}
