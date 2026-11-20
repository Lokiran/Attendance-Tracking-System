using System.Text.Json.Serialization;

namespace Ats.Domain;

// Enumerations of backend/app/models/enums.py. Each member's string value (stored in the database as VARCHAR with a
// CHECK constraint, and written to JSON) is given explicitly and must never change.

public enum Role
{
    [JsonStringEnumMemberName("EMPLOYEE")] Employee,
    [JsonStringEnumMemberName("MANAGER")] Manager,
    [JsonStringEnumMemberName("ADMIN")] Admin,
}

/// <summary>Legacy check-in sessions (unused since attendance comes from imported timesheets).</summary>
public enum SessionStatus
{
    [JsonStringEnumMemberName("OPEN")] Open,
    [JsonStringEnumMemberName("COMPLETED")] Completed,
    [JsonStringEnumMemberName("MISSING_CHECKOUT")] MissingCheckout,
    [JsonStringEnumMemberName("VOIDED")] Voided,
}

public enum SessionSource
{
    [JsonStringEnumMemberName("WEB")] Web,
    [JsonStringEnumMemberName("CORRECTION")] Correction,
    [JsonStringEnumMemberName("ADMIN")] Admin,
    [JsonStringEnumMemberName("SEED")] Seed,
    [JsonStringEnumMemberName("AUTO_WIFI")] AutoWifi,
    [JsonStringEnumMemberName("AUTO_TEAMS")] AutoTeams,
}

public enum WifiEventType
{
    [JsonStringEnumMemberName("CONNECT")] Connect,
    [JsonStringEnumMemberName("DISCONNECT")] Disconnect,
    [JsonStringEnumMemberName("SEEN")] Seen,
}

/// <summary>How much of a day was worked (stored in attendance_daily.status). Where it was worked is <see cref="WorkMode"/>.</summary>
public enum DailyStatus
{
    [JsonStringEnumMemberName("PRESENT")] Present,
    [JsonStringEnumMemberName("HALF_DAY")] HalfDay,
    [JsonStringEnumMemberName("ABSENT")] Absent,
    [JsonStringEnumMemberName("LEAVE")] Leave,
    [JsonStringEnumMemberName("HOLIDAY")] Holiday,
    [JsonStringEnumMemberName("WEEKEND")] Weekend,
    [JsonStringEnumMemberName("NO_DATA")] NoData,
}

/// <summary>Where a day was worked (WFO tracking).</summary>
public enum WorkMode
{
    [JsonStringEnumMemberName("OFFICE")] Office,
    [JsonStringEnumMemberName("WFH")] Wfh,
    [JsonStringEnumMemberName("LEAVE")] Leave,
    [JsonStringEnumMemberName("HOLIDAY")] Holiday,
    [JsonStringEnumMemberName("WEEKOFF")] Weekoff,
}

/// <summary>Which signal decided a day's work mode (highest priority first).</summary>
public enum WfoSource
{
    [JsonStringEnumMemberName("MANUAL_OVERRIDE")] ManualOverride,
    [JsonStringEnumMemberName("UPLOAD_COLUMN")] UploadColumn,
    [JsonStringEnumMemberName("BADGE")] Badge,
    [JsonStringEnumMemberName("WIFI")] Wifi,
    [JsonStringEnumMemberName("CHECK_IN")] CheckIn,
    [JsonStringEnumMemberName("INFERRED")] Inferred,
    [JsonStringEnumMemberName("CALENDAR")] Calendar,
}

public enum WorkLocation
{
    [JsonStringEnumMemberName("OFFICE")] Office,
    [JsonStringEnumMemberName("WFH")] Wfh,
}

public enum UploadStatus
{
    [JsonStringEnumMemberName("PREVIEW")] Preview,
    [JsonStringEnumMemberName("COMMITTED")] Committed,
    [JsonStringEnumMemberName("DISCARDED")] Discarded,
}

/// <summary>Computed for each employee-day from the imported timesheets (never stored).</summary>
public enum DayStatus
{
    [JsonStringEnumMemberName("PRESENT")] Present,
    [JsonStringEnumMemberName("WFH")] Wfh,
    [JsonStringEnumMemberName("ABSENT")] Absent,
    [JsonStringEnumMemberName("LEAVE")] Leave,
    [JsonStringEnumMemberName("HOLIDAY")] Holiday,
    [JsonStringEnumMemberName("WEEKEND")] Weekend,
    [JsonStringEnumMemberName("NO_TIMESHEET")] NoTimesheet,
    [JsonStringEnumMemberName("NOT_SCHEDULED")] NotScheduled,
}

/// <summary>Retired with the leave feature; only the legacy leave_records table uses it.</summary>
public enum LeaveType
{
    [JsonStringEnumMemberName("ANNUAL")] Annual,
    [JsonStringEnumMemberName("SICK")] Sick,
    [JsonStringEnumMemberName("CASUAL")] Casual,
    [JsonStringEnumMemberName("UNPAID")] Unpaid,
    [JsonStringEnumMemberName("OTHER")] Other,
}

public enum RequestStatus
{
    [JsonStringEnumMemberName("PENDING")] Pending,
    [JsonStringEnumMemberName("APPROVED")] Approved,
    [JsonStringEnumMemberName("REJECTED")] Rejected,
    [JsonStringEnumMemberName("CANCELLED")] Cancelled,
}

public enum CorrectionType
{
    [JsonStringEnumMemberName("ADD_SESSION")] AddSession,
    [JsonStringEnumMemberName("MODIFY_SESSION")] ModifySession,
}
