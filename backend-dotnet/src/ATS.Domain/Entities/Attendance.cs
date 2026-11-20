using System.Text.Json.Nodes;

namespace Ats.Domain.Entities;

/// <summary>
/// An imported attendance / badge file. Parsed rows are kept, so committing applies exactly what the preview showed.
/// </summary>
public class AttendanceUpload : ICreatedAt
{
    public int Id { get; set; }

    public string Filename { get; set; } = "";

    public string FileSha256 { get; set; } = "";

    public int FileSize { get; set; }

    public UploadStatus Status { get; set; } = UploadStatus.Preview;

    public int RowCount { get; set; }

    public int ErrorCount { get; set; }

    /// <summary>Preview figures (stored JSON, returned as stored).</summary>
    public JsonObject? Summary { get; set; }

    /// <summary>What the commit applied / skipped (stored JSON).</summary>
    public JsonObject? CommitSummary { get; set; }

    public bool OverwriteOverrides { get; set; }

    public int? UploadedById { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public int? CommittedById { get; set; }

    public DateTimeOffset? CommittedAt { get; set; }

    /// <summary>Dates the timesheet covers once committed (a scheduled day in the period missing from the file is absent).</summary>
    public DateOnly? PeriodStart { get; set; }

    public DateOnly? PeriodEnd { get; set; }
}

/// <summary>One parsed row of an upload; <see cref="Error"/> is set when the row cannot be applied.</summary>
public class AttendanceUploadRow
{
    public long Id { get; set; }

    public int UploadId { get; set; }

    /// <summary>As in the file (header = row 1).</summary>
    public int RowNumber { get; set; }

    /// <summary>Employee code or email as written.</summary>
    public string? EmployeeRef { get; set; }

    public int? EmployeeId { get; set; }

    public DateOnly? WorkDate { get; set; }

    public string? RawWorkMode { get; set; }

    public WorkMode? WorkMode { get; set; }

    public string? RawSource { get; set; }

    public bool IsBadge { get; set; }

    public int? Minutes { get; set; }

    public DateTimeOffset? FirstIn { get; set; }

    public DateTimeOffset? LastOut { get; set; }

    public string? Error { get; set; }
}

/// <summary>A person's device was seen on the office Wi-Fi during one time bucket (unused by the API so far).</summary>
public class WifiEvent : ICreatedAt
{
    public long Id { get; set; }

    public int EmployeeId { get; set; }

    public DateTimeOffset TsBucket { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public WifiEventType EventType { get; set; }

    public string Source { get; set; } = "";

    public string? AccessPoint { get; set; }

    public int? SiteId { get; set; }

    public int? DeviceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A person's Microsoft Teams presence in one poll bucket (unused by the API so far).</summary>
public class PresenceEvent : ICreatedAt
{
    public long Id { get; set; }

    public int EmployeeId { get; set; }

    public DateTimeOffset TsBucket { get; set; }

    public DateTimeOffset PolledAt { get; set; }

    public string Availability { get; set; } = "";

    public string? Activity { get; set; }

    public string Source { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// What the timesheets (and admin overrides) say about one person on one office-local work date, plus the stored
/// WFO classification. Evidence columns are null when that source says nothing about the day.
/// </summary>
public class AttendanceDaily : ITimestamped
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public DateOnly WorkDate { get; set; }

    public DateTimeOffset? FirstIn { get; set; }

    public DateTimeOffset? LastOut { get; set; }

    public int OfficeMinutes { get; set; }

    public int WfhMinutes { get; set; }

    public DailyStatus Status { get; set; }

    public bool Corrected { get; set; }

    public DateTimeOffset? ComputedAt { get; set; }

    public WorkMode? WorkMode { get; set; }

    public WfoSource? WfoSource { get; set; }

    public List<string> Conflicts { get; set; } = [];

    public WorkMode? UploadWorkMode { get; set; }

    public int? UploadMinutes { get; set; }

    public int? BadgeMinutes { get; set; }

    public int? WifiMinutes { get; set; }

    public int? UploadId { get; set; }

    public WorkMode? OverrideWorkMode { get; set; }

    public string? OverrideNote { get; set; }

    public int? OverriddenById { get; set; }

    public DateTimeOffset? OverriddenAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Legacy: a request to change one day's attendance (unused since the correction feature was removed).</summary>
public class AttendanceDailyCorrection : ITimestamped
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public DateOnly WorkDate { get; set; }

    public DateTimeOffset? RequestedFirstIn { get; set; }

    public DateTimeOffset? RequestedLastOut { get; set; }

    public DailyStatus? RequestedStatus { get; set; }

    public string Reason { get; set; } = "";

    public JsonObject? OriginalValues { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;

    public int? ReviewedById { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ReviewerComment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
