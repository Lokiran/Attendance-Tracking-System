from datetime import date, datetime, time

from pydantic import BaseModel, Field

from app.models import DayStatus, SessionSource, SessionStatus, WorkLocation
from app.schemas.common import ORMModel
from app.schemas.wfo import WfoRangeSummary
from app.services.attendance_rules import DayRecord, Summary


class SessionOut(ORMModel):
    id: int
    employee_id: int
    check_in_at: datetime
    check_out_at: datetime | None
    break_minutes: int
    net_minutes: int | None
    status: SessionStatus
    source: SessionSource
    notes: str | None
    work_location: WorkLocation | None = None
    voided_at: datetime | None
    void_reason: str | None


class CheckInRequest(BaseModel):
    work_location: WorkLocation | None = Field(None, description="Office or WFH; used for WFO tracking")


class CheckOutRequest(BaseModel):
    break_minutes: int = Field(0, ge=0, le=720, description="Unpaid break taken during this session")


class DayRecordOut(BaseModel):
    date: date
    status: DayStatus
    expected: bool
    scheduled_workday: bool
    required_minutes: int
    net_minutes: int
    session_count: int
    completed_sessions: int
    first_check_in: datetime | None
    last_check_out: datetime | None
    has_active_session: bool
    unresolved_sessions: int
    holiday_name: str | None
    exceptions: list[str]

    @classmethod
    def of(cls, r: DayRecord) -> "DayRecordOut":
        return cls(
            date=r.date, status=r.status, expected=r.expected, scheduled_workday=r.scheduled_workday,
            required_minutes=r.required_minutes, net_minutes=r.net_minutes, session_count=r.session_count,
            completed_sessions=r.completed_sessions, first_check_in=r.first_check_in,
            last_check_out=r.last_check_out, has_active_session=r.has_active_session,
            unresolved_sessions=r.unresolved_sessions, holiday_name=r.holiday_name,
            exceptions=list(r.exceptions),
        )


class SummaryOut(BaseModel):
    start: date
    end: date
    expected_days: int
    present_days: int
    present_expected_days: int
    extra_present_days: int
    absent_days: int
    holiday_days: int
    weekend_days: int
    not_scheduled_days: int
    incomplete_days: int
    not_yet_checked_in_days: int
    net_minutes: int
    required_minutes: int
    variance_minutes: int
    average_net_minutes: int | None
    attendance_percentage: float | None
    attended_days_for_average: int
    unresolved_sessions: int
    late_days: int
    # WFO figures for the same range (wfo_days, wfh_days, wfo_percentage, weekly breakdown, compliance ...)
    wfo: WfoRangeSummary | None = None

    @classmethod
    def of(cls, s: Summary) -> "SummaryOut":
        return cls(
            start=s.start, end=s.end, expected_days=s.expected_days, present_days=s.present_days,
            present_expected_days=s.present_expected_days, extra_present_days=s.extra_present_days,
            absent_days=s.absent_days, holiday_days=s.holiday_days,
            weekend_days=s.weekend_days, not_scheduled_days=s.not_scheduled_days,
            incomplete_days=s.incomplete_days, not_yet_checked_in_days=s.not_yet_checked_in_days,
            net_minutes=s.net_minutes, required_minutes=s.required_minutes, variance_minutes=s.variance_minutes,
            average_net_minutes=s.average_net_minutes, attendance_percentage=s.attendance_percentage,
            attended_days_for_average=s.attended_days_for_average, unresolved_sessions=s.unresolved_sessions,
            late_days=s.late_days,
        )


class ScheduleBrief(BaseModel):
    name: str
    start_time: time
    end_time: time
    required_minutes: int
    working_days: list[int]


class CurrentStatusOut(BaseModel):
    server_time: datetime
    today: date
    office_timezone: str
    today_record: DayRecordOut | None
    active_session: SessionOut | None
    unresolved_session: SessionOut | None
    schedule: ScheduleBrief | None
    max_session_minutes: int


class DayDetailOut(BaseModel):
    record: DayRecordOut | None
    sessions: list[SessionOut]
    voided_sessions: list[SessionOut]


class DaysPage(BaseModel):
    items: list[DayRecordOut]
    total: int
    page: int
    page_size: int
