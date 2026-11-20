namespace Ats.Domain.Entities;

/// <summary>
/// Working pattern. Resolution for a day: employee-specific &gt; department &gt; organisation default (both targets
/// null); within a tier the assignment effective on that date wins.
/// </summary>
public class WorkSchedule : ITimestamped
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public int? EmployeeId { get; set; }

    public int? DepartmentId { get; set; }

    public TimeOnly StartTime { get; set; }

    /// <summary>Earlier than <see cref="StartTime"/>: an overnight shift.</summary>
    public TimeOnly EndTime { get; set; }

    public int RequiredMinutes { get; set; }

    /// <summary>ISO weekdays 1 = Monday .. 7 = Sunday (a JSON column).</summary>
    public List<int> WorkingDays { get; set; } = [];

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A date can be a holiday once organisation-wide and once per department.</summary>
public class Holiday : ICreatedAt
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public DateOnly HolidayDate { get; set; }

    public int? DepartmentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Organisation-wide attendance rules: a single row with id 1.</summary>
public class AttendancePolicy
{
    public static readonly IReadOnlyList<string> DefaultWfhPresenceStates = ["Available", "Busy", "InAMeeting"];

    public int Id { get; set; } = 1;

    public int MinPresentMinutes { get; set; }

    public bool CountIncompleteAsPresent { get; set; }

    public int MaxSessionMinutes { get; set; } = 960;

    public int LateGraceMinutes { get; set; } = 15;

    public int CorrectionWindowDays { get; set; } = 30;

    public bool ManagerCanApprove { get; set; } = true;

    public bool AutoAttendanceEnabled { get; set; }

    public bool AutoShadowMode { get; set; } = true;

    public int GapMinutes { get; set; } = 30;

    public int FullDayMinutes { get; set; } = 480;

    public int HalfDayMinutes { get; set; } = 240;

    public int WifiPollMinutes { get; set; } = 3;

    public int PresencePollMinutes { get; set; } = 5;

    public int NoDataAlertMinutes { get; set; } = 15;

    public int RawRetentionDays { get; set; } = 90;

    public int MinWfoDaysPerWeek { get; set; } = 3;

    public int MinWfoDaysPerMonth { get; set; } = 12;

    public int MinOfficeMinutesForWfoDay { get; set; } = 240;

    public List<string> WfhPresenceStates { get; set; } = [.. DefaultWfhPresenceStates];

    public DateTimeOffset UpdatedAt { get; set; }

    public int? UpdatedById { get; set; }

    public string? Notes { get; set; }
}
