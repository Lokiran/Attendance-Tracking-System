using System.Text.Json.Nodes;

namespace Ats.Domain.Entities;

// Tables of the retired check-in, correction and leave features. Nothing in the app reads or writes them any more;
// the mappings stay so the schema matches and existing rows are never dropped without an approved migration.

/// <summary>One check-in / check-out pair (legacy).</summary>
public class AttendanceSession : ITimestamped
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public DateTimeOffset CheckInAt { get; set; }

    public DateTimeOffset? CheckOutAt { get; set; }

    public int BreakMinutes { get; set; }

    public int? NetMinutes { get; set; }

    public SessionStatus Status { get; set; }

    public SessionSource Source { get; set; } = SessionSource.Web;

    public WorkLocation? WorkLocation { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public string? VoidReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Request to add or fix a check-in session (legacy).</summary>
public class AttendanceCorrection : ITimestamped
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public DateOnly AttendanceDate { get; set; }

    public CorrectionType RequestType { get; set; }

    public int? OriginalSessionId { get; set; }

    public JsonObject? OriginalValues { get; set; }

    public DateTimeOffset RequestedCheckInAt { get; set; }

    public DateTimeOffset RequestedCheckOutAt { get; set; }

    public int RequestedBreakMinutes { get; set; }

    public string Reason { get; set; } = "";

    public RequestStatus Status { get; set; } = RequestStatus.Pending;

    public int? ReviewedById { get; set; }

    public string? ReviewerComment { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public int? ResultingSessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Leave request (legacy; leave now only comes from imported files or admin overrides).</summary>
public class LeaveRecord : ITimestamped
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public LeaveType LeaveType { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;

    public string? Reason { get; set; }

    public int? ReviewedById { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ReviewerComment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
