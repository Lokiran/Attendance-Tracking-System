using System.Text.Json.Nodes;
using Ats.Application.Auth;
using Ats.Domain;
using Ats.Domain.Entities;

namespace Ats.Application.Admin;

/// <summary>A page of results (<c>Page[T]</c>).</summary>
public sealed record PagedResult<T>(List<T> Items, int Total, int Page, int PageSize);

public sealed record UserAdminOut(int Id, string EmployeeCode, string FullName, string Email, Role Role, string? JobTitle,
                                  DepartmentBrief? Department, ManagerBrief? Manager, bool IsActive, bool AttendanceTracked,
                                  DateOnly DateJoined, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The user with <see cref="User.Department"/> and <see cref="User.Manager"/> loaded.</summary>
    public static UserAdminOut Of(User u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return new(u.Id, u.EmployeeCode, u.FullName, u.Email, u.Role, u.JobTitle,
                   u.Department is { } d ? new DepartmentBrief(d.Id, d.Name) : null,
                   u.Manager is { } m ? new ManagerBrief(m.Id, m.FullName, m.EmployeeCode) : null,
                   u.IsActive, u.AttendanceTracked, u.DateJoined, u.CreatedAt, u.UpdatedAt);
    }
}

public sealed record ScheduleOut(int Id, string Name, int? EmployeeId, int? DepartmentId, TimeOnly StartTime, TimeOnly EndTime,
                                 int RequiredMinutes, List<int> WorkingDays, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string TargetLabel);

public sealed record AuditOut(long Id, int? ActorUserId, string? ActorName, string Action, string EntityType, string? EntityId,
                              JsonObject? BeforeValues, JsonObject? AfterValues, string? RequestId, string? IpAddress, DateTimeOffset CreatedAt);

public sealed record AutoAttendanceSettingsOut(bool AutoAttendanceEnabled, bool AutoShadowMode, int GapMinutes, int FullDayMinutes,
                                               int HalfDayMinutes, int WifiPollMinutes, int PresencePollMinutes, int NoDataAlertMinutes,
                                               int RawRetentionDays, List<string> WfhPresenceStates, DateTimeOffset UpdatedAt)
{
    public static AutoAttendanceSettingsOut Of(AttendancePolicy p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.AutoAttendanceEnabled, p.AutoShadowMode, p.GapMinutes, p.FullDayMinutes, p.HalfDayMinutes, p.WifiPollMinutes,
                   p.PresencePollMinutes, p.NoDataAlertMinutes, p.RawRetentionDays, [.. p.WfhPresenceStates], p.UpdatedAt);
    }
}
