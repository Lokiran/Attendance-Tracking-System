using Ats.Domain;

namespace Ats.Application.Dashboard;

// Shapes of backend/app/schemas/dashboard.py (field order as declared; snake_case on the wire).

/// <summary>Mutually exclusive status counts for one date; they always sum to headcount.</summary>
public sealed record Snapshot(int Present, int Wfh, int Absent, int Leave, int NoTimesheet, int Off);

public sealed record PeriodStats(int ExpectedDays, int PresentExpectedDays, int PresentDays, int AbsentDays, int WfhDays, int LeaveDays,
                                 int NoTimesheetDays, int LateDays, int NetMinutes, int RequiredMinutes, int HoursDays,
                                 int HoursRequiredMinutes, int VarianceMinutes, int? AverageNetMinutes, double? AttendancePercentage,
                                 int CountedDays);

public sealed record BandCount(string Label, int Count);

public sealed record OverviewOut(DateTimeOffset GeneratedAt, DateOnly OnDate, DateOnly Start, DateOnly End, int Headcount, Snapshot Snapshot,
                                 PeriodStats Period, IReadOnlyList<BandCount> AttendanceBands, DateOnly? TimesheetThrough);

public sealed record RowDay(DayStatus? Status, DateTimeOffset? FirstIn, DateTimeOffset? LastOut, int NetMinutes, bool HoursKnown,
                            WorkMode? WorkMode, IReadOnlyList<string> Exceptions);

public sealed record EmployeeRow(int EmployeeId, string EmployeeCode, string FullName, string? JobTitle, Role Role, bool IsActive,
                                 int? DepartmentId, string? DepartmentName, int? ManagerId, string? ManagerName, RowDay Day,
                                 PeriodStats Period, int ExceptionCount);

public sealed record EmployeeRowsPage(IReadOnlyList<EmployeeRow> Items, int Total, int Page, int PageSize);

public sealed record TrendBucket(DateOnly Start, DateOnly End, string Label, int Expected, int PresentExpected, int Present, int Absent,
                                 int Wfh, int NetMinutes, int RequiredMinutes, int HoursRequiredMinutes, int Attended, int Late,
                                 int ShortHours, double? AttendancePercentage, int? AverageNetMinutes);

public sealed record TrendsOut(string Granularity, int Headcount, IReadOnlyList<TrendBucket> Items);

public sealed record ExceptionRow(int EmployeeId, string EmployeeCode, string FullName, string? DepartmentName, DateOnly Date,
                                  DayStatus Status, IReadOnlyList<string> Exceptions, DateTimeOffset? FirstIn, DateTimeOffset? LastOut,
                                  int NetMinutes, bool HoursKnown, int RequiredMinutes);

public sealed record ExceptionsOut(IReadOnlyDictionary<string, int> Counts, IReadOnlyList<ExceptionRow> Items, int Total, int Page, int PageSize);

public sealed record DepartmentStats(int? DepartmentId, string DepartmentName, string? HeadName, int Headcount, Snapshot Snapshot, PeriodStats Period);

public sealed record ManagerStats(int ManagerId, string ManagerName, string EmployeeCode, string? DepartmentName, int TeamSize,
                                  int DirectReports, Snapshot Snapshot, PeriodStats Period);

public sealed record HierarchyNode(int Id, string EmployeeCode, string FullName, Role Role, string? JobTitle, bool IsActive,
                                   int DirectReports, IReadOnlyList<HierarchyNode> Children);

public sealed record HeadBrief(int Id, string FullName);

public sealed record HierarchyDepartment(int? Id, string Name, string? Description, HeadBrief? Head, int MemberCount,
                                         IReadOnlyList<HierarchyNode> Nodes);

public sealed record HierarchyOut(int TotalPeople, IReadOnlyList<HierarchyDepartment> Departments);

public sealed record TeamMember(int Id, string EmployeeCode, string FullName, Role Role, string? JobTitle, string? DepartmentName,
                                int? ManagerId, string? ManagerName, bool IsActive);
