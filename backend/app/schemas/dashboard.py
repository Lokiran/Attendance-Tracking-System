from datetime import date, datetime

from pydantic import BaseModel

from app.models import DayStatus, Role


class Snapshot(BaseModel):
    """Mutually exclusive status counts for one date; they always sum to headcount."""
    present: int
    absent: int
    not_checked_in: int
    incomplete: int
    off: int
    checked_in_now: int


class PeriodStats(BaseModel):
    expected_days: int
    present_expected_days: int
    present_days: int
    absent_days: int
    incomplete_days: int
    late_days: int
    unresolved_sessions: int
    net_minutes: int
    required_minutes: int
    variance_minutes: int
    attended_days_for_average: int
    average_net_minutes: int | None
    attendance_percentage: float | None


class OverviewOut(BaseModel):
    generated_at: datetime
    on_date: date
    start: date
    end: date
    headcount: int
    snapshot: Snapshot
    period: PeriodStats
    pending_corrections: int


class RowDay(BaseModel):
    status: DayStatus | None
    first_check_in: datetime | None
    last_check_out: datetime | None
    net_minutes: int
    has_active_session: bool
    exceptions: list[str]


class EmployeeRow(BaseModel):
    employee_id: int
    employee_code: str
    full_name: str
    job_title: str | None
    role: Role
    is_active: bool
    department_id: int | None
    department_name: str | None
    manager_id: int | None
    manager_name: str | None
    day: RowDay
    period: PeriodStats
    exception_count: int


class EmployeeRowsPage(BaseModel):
    items: list[EmployeeRow]
    total: int
    page: int
    page_size: int


class TrendBucket(BaseModel):
    start: date
    end: date
    label: str
    expected: int
    present_expected: int
    present: int
    absent: int
    incomplete: int
    net_minutes: int
    required_minutes: int
    attended: int
    late: int
    missing_checkout: int
    short_hours: int
    attendance_percentage: float | None
    average_net_minutes: int | None


class TrendsOut(BaseModel):
    granularity: str
    headcount: int
    items: list[TrendBucket]


class ExceptionRow(BaseModel):
    employee_id: int
    employee_code: str
    full_name: str
    department_name: str | None
    date: date
    status: DayStatus
    exceptions: list[str]
    first_check_in: datetime | None
    last_check_out: datetime | None
    net_minutes: int
    required_minutes: int


class ExceptionsOut(BaseModel):
    counts: dict[str, int]
    items: list[ExceptionRow]
    total: int
    page: int
    page_size: int


class DepartmentStats(BaseModel):
    department_id: int | None
    department_name: str
    head_name: str | None
    headcount: int
    snapshot: Snapshot
    period: PeriodStats


class ManagerStats(BaseModel):
    manager_id: int
    manager_name: str
    employee_code: str
    department_name: str | None
    team_size: int
    direct_reports: int
    snapshot: Snapshot
    period: PeriodStats


class HierarchyNode(BaseModel):
    id: int
    employee_code: str
    full_name: str
    role: Role
    job_title: str | None
    is_active: bool
    direct_reports: int
    children: list["HierarchyNode"]


class HierarchyDepartment(BaseModel):
    id: int | None
    name: str
    description: str | None
    head: dict | None
    member_count: int
    nodes: list[HierarchyNode]


class HierarchyOut(BaseModel):
    total_people: int
    departments: list[HierarchyDepartment]


class TeamMember(BaseModel):
    id: int
    employee_code: str
    full_name: str
    role: Role
    job_title: str | None
    department_name: str | None
    manager_id: int | None
    manager_name: str | None
    is_active: bool
