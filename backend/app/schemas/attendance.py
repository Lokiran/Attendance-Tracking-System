from datetime import date, datetime, time

from pydantic import BaseModel

from app.models import DayStatus, WfoSource, WorkMode
from app.schemas.wfo import WfoRangeSummary
from app.services.attendance_rules import DayRecord, Summary


class DayRecordOut(BaseModel):
    date: date
    status: DayStatus
    expected: bool
    scheduled_workday: bool
    required_minutes: int
    net_minutes: int            # office minutes from the timesheet (0 when not recorded)
    hours_known: bool           # False: the timesheet listed the day without hours
    work_mode: WorkMode | None
    first_in: datetime | None
    last_out: datetime | None
    holiday_name: str | None
    exceptions: list[str]

    @classmethod
    def of(cls, r: DayRecord) -> "DayRecordOut":
        return cls(
            date=r.date, status=r.status, expected=r.expected, scheduled_workday=r.scheduled_workday,
            required_minutes=r.required_minutes, net_minutes=r.net_minutes, hours_known=r.hours_known,
            work_mode=r.work_mode, first_in=r.first_in, last_out=r.last_out, holiday_name=r.holiday_name,
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
    wfh_days: int
    leave_days: int
    holiday_days: int
    weekend_days: int
    not_scheduled_days: int
    no_timesheet_days: int
    net_minutes: int
    required_minutes: int
    hours_days: int                 # present days with recorded hours
    hours_required_minutes: int     # shift minutes of those days
    variance_minutes: int           # net_minutes - hours_required_minutes
    average_net_minutes: int | None
    attendance_percentage: float | None
    late_days: int
    # WFO figures for the same range (wfo_days, wfh_days, wfo_percentage, weekly breakdown, compliance ...)
    wfo: WfoRangeSummary | None = None

    @classmethod
    def of(cls, s: Summary) -> "SummaryOut":
        return cls(
            start=s.start, end=s.end, expected_days=s.expected_days, present_days=s.present_days,
            present_expected_days=s.present_expected_days, extra_present_days=s.extra_present_days,
            absent_days=s.absent_days, wfh_days=s.wfh_days, leave_days=s.leave_days, holiday_days=s.holiday_days,
            weekend_days=s.weekend_days, not_scheduled_days=s.not_scheduled_days,
            no_timesheet_days=s.no_timesheet_days, net_minutes=s.net_minutes, required_minutes=s.required_minutes,
            hours_days=s.hours_days, hours_required_minutes=s.hours_required_minutes,
            variance_minutes=s.variance_minutes, average_net_minutes=s.average_net_minutes,
            attendance_percentage=s.attendance_percentage, late_days=s.late_days,
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
    schedule: ScheduleBrief | None
    timesheet_through: date | None     # last date any committed timesheet covers
    my_timesheet_through: date | None  # last date a committed timesheet listing me covers
    last_import_at: datetime | None


class TimesheetEntryOut(BaseModel):
    """What the timesheets and overrides recorded for one employee-day."""
    work_mode: WorkMode | None
    wfo_source: WfoSource | None
    upload_work_mode: WorkMode | None
    upload_minutes: int | None
    badge_minutes: int | None
    first_in: datetime | None
    last_out: datetime | None
    upload_filename: str | None
    override_work_mode: WorkMode | None
    override_note: str | None
    overridden_by_name: str | None
    overridden_at: datetime | None
    conflicts: list[str]


class DayDetailOut(BaseModel):
    record: DayRecordOut | None
    timesheet: TimesheetEntryOut | None


class DaysPage(BaseModel):
    items: list[DayRecordOut]
    total: int
    page: int
    page_size: int
