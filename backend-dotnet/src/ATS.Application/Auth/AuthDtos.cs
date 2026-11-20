using Ats.Domain;
using Ats.Domain.Entities;

namespace Ats.Application.Auth;

// Request and response shapes of backend/app/schemas/auth.py (snake_case on the wire).

public sealed class LoginRequest
{
    public string? Email { get; set; }

    public string? Password { get; set; }
}

/// <summary>An email, or an employee ID ("10001", "Intern - 10003"): people added from a timesheet have placeholder emails.</summary>
public sealed class DevLoginRequest
{
    public string? Email { get; set; }
}

public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }

    public string? NewPassword { get; set; }
}

/// <summary>Which sign-in methods the login page offers.</summary>
public sealed record ProvidersOut(bool Password, bool Entra, bool Dev);

public sealed record MessageResponse(string Message);

public sealed record DepartmentBrief(int Id, string Name);

public sealed record ManagerBrief(int Id, string FullName, string EmployeeCode);

public sealed record Profile(int Id, string EmployeeCode, string FullName, string Email, Role Role, string? JobTitle,
                             DepartmentBrief? Department, ManagerBrief? Manager, DateOnly DateJoined, bool IsActive,
                             bool AttendanceTracked)
{
    /// <summary>The user with department and manager loaded.</summary>
    public static Profile Of(User u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return new Profile(u.Id, u.EmployeeCode, u.FullName, u.Email, u.Role, u.JobTitle,
                           u.Department is { } d ? new DepartmentBrief(d.Id, d.Name) : null,
                           u.Manager is { } m ? new ManagerBrief(m.Id, m.FullName, m.EmployeeCode) : null,
                           u.DateJoined, u.IsActive, u.AttendanceTracked);
    }
}

/// <summary>
/// Current user, the CSRF token to echo in X-CSRF-Token, the office time zone, whether the user manages anyone, and the
/// last date any committed timesheet covers (pages open on that month while the current month has none).
/// </summary>
public sealed record MeResponse(Profile User, string CsrfToken, string OfficeTimezone, bool HasReports,
                                DateOnly? LatestTimesheetDate);
