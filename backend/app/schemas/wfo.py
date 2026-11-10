from datetime import date, datetime

from pydantic import BaseModel, Field

from app.models import WfoSource, WorkMode


class WfoPolicyOut(BaseModel):
    min_days_per_week: int
    min_days_per_month: int
    min_office_minutes: int


class WfoPolicyUpdate(BaseModel):
    min_days_per_week: int | None = Field(None, ge=0, le=7)
    min_days_per_month: int | None = Field(None, ge=0, le=31)
    min_office_minutes: int | None = Field(None, ge=0, le=1440)


class WfoWeek(BaseModel):
    week_start: date
    week_end: date
    label: str
    working_days: int
    wfo_days: int
    required: int
    status: str  # MET | SHORT | IN_PROGRESS | UPCOMING
    shortfall: int


class WfoSummaryOut(BaseModel):
    working_days: int
    standard_days: int
    wfo_days: int
    wfh_days: int
    office_days_below_minimum: int
    leave_days: int
    holiday_days: int
    weekoff_days: int
    required_days: int
    shortfall_days: int
    wfo_percentage: float | None
    compliance: str  # COMPLIANT | NON_COMPLIANT


class WfoRangeSummary(WfoSummaryOut):
    """WFO figures added to the existing attendance summary."""
    weekly_wfo_breakdown: list[WfoWeek]


class EmployeeBrief(BaseModel):
    id: int
    employee_code: str
    full_name: str
    department_name: str | None
    manager_id: int | None
    manager_name: str | None


class WfoDay(BaseModel):
    date: date
    work_mode: WorkMode | None
    wfo_source: WfoSource | None
    is_wfo_day: bool
    office_minutes: int
    wfh_minutes: int
    conflicts: list[str]
    scheduled: bool
    overridden: bool
    override_note: str | None
    in_progress: bool
    future: bool


class EmployeeWfoOut(BaseModel):
    employee: EmployeeBrief
    month: str
    start: date
    end: date
    today: date
    policy: WfoPolicyOut
    summary: WfoSummaryOut
    weekly_wfo_breakdown: list[WfoWeek]
    days: list[WfoDay]


class WfoRow(WfoSummaryOut):
    employee: EmployeeBrief


class WfoTotals(BaseModel):
    headcount: int
    working_days: int
    wfo_days: int
    wfh_days: int
    wfo_percentage: float | None
    compliant: int
    non_compliant: int


class TeamWfoOut(BaseModel):
    month: str
    start: date
    end: date
    policy: WfoPolicyOut
    totals: WfoTotals
    rows: list[WfoRow]


class WfoManagerGroup(BaseModel):
    manager_id: int | None
    manager_name: str
    department_name: str | None
    totals: WfoTotals
    rows: list[WfoRow]


class OrgWfoOut(BaseModel):
    month: str
    start: date
    end: date
    policy: WfoPolicyOut
    totals: WfoTotals
    groups: list[WfoManagerGroup]


class OverrideRequest(BaseModel):
    work_mode: WorkMode
    note: str | None = Field(None, max_length=500)


class OverrideOut(BaseModel):
    employee_id: int
    work_date: date
    work_mode: WorkMode | None
    wfo_source: WfoSource | None
    overridden: bool
    override_note: str | None
    overridden_at: datetime | None


class UploadOut(BaseModel):
    id: int
    filename: str
    status: str  # PREVIEW | COMMITTED | DISCARDED
    file_size: int
    row_count: int
    error_count: int
    summary: dict | None          # preview: counts by work mode, conflicts, protected overrides, errors
    commit_summary: dict | None
    overwrite_overrides: bool
    uploaded_by_name: str | None = None
    created_at: datetime
    committed_at: datetime | None
    period_start: date | None = None   # dates this timesheet covers once committed
    period_end: date | None = None

    model_config = {"from_attributes": True}


class CommitRequest(BaseModel):
    overwrite_overrides: bool = Field(False, description="Replace days an Admin overrode (they are skipped otherwise)")


class CreateEmployeesRequest(BaseModel):
    employee_refs: list[str] | None = Field(None, max_length=5000,
                                            description="IDs to add (default: every unknown ID that can be added)")
