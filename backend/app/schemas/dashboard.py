from datetime import date, datetime

from pydantic import BaseModel

from app.models import DayStatus, Role, WorkMode


class Snapshot(BaseModel):
    """Mutually exclusive status counts for one date; they always sum to headcount."""
    present: int
    wfh: int
    absent: int
    leave: int
    no_timesheet: int
    off: int


class PeriodStats(BaseModel):
    expected_days: int
    present_expected_days: int
    present_days: int
    absent_days: int
    wfh_days: int
    leave_days: int
    no_timesheet_days: int
    late_days: int
    net_minutes: int
    required_minutes: int
    hours_days: int                 # present days with recorded hours
    hours_required_minutes: int     # shift minutes of those days
    variance_minutes: int           # net_minutes - hours_required_minutes
    average_net_minutes: int | None
    attendance_percentage: float | None


class BandCount(BaseModel):
    label: str
    count: int


class OverviewOut(BaseModel):
    generated_at: datetime
    on_date: date
    start: date
    end: date
    headcount: int
    snapshot: Snapshot
    period: PeriodStats
    attendance_bands: list[BandCount]
    timesheet_through: date | None   # last date covered by a committed timesheet


class RowDay(BaseModel):
    status: DayStatus | None
    first_in: datetime | None
    last_out: datetime | None
    net_minutes: int
    hours_known: bool
    work_mode: WorkMode | None
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
    wfh: int
    net_minutes: int
    required_minutes: int
    hours_required_minutes: int
    attended: int                   # present days with recorded hours
    late: int
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
    first_in: datetime | None
    last_out: datetime | None
    net_minutes: int
    hours_known: bool
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
