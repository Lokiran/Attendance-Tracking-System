"""Per-employee attendance queries. Attendance comes from the timesheets the Admin imports (there is no
check-in / check-out): see ``app.services.attendance_data`` and ``app.services.attendance_rules``."""

from datetime import date

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.timeutil import local_date, office_tz
from app.models import AttendanceDaily, AttendanceUpload, User, WorkSchedule
from app.services.attendance_data import compute_records, coverage, employee_data, latest_import_through, schedule_data
from app.services.attendance_rules import DayRecord, Summary, resolve_schedule, summarize


def records_for(db: Session, employee: User, start: date, end: date) -> list[DayRecord]:
    return compute_records(db, [employee], start, end)[employee.id]


def summary_for(db: Session, employee: User, start: date, end: date) -> tuple[Summary, list[DayRecord]]:
    records = records_for(db, employee, start, end)
    return summarize(records, start, end), records


def current_status(db: Session, employee: User) -> dict:
    """Today, the shift, and how far the imported timesheets reach."""
    now = clock.utcnow()
    tz = office_tz()
    today = local_date(now, tz)
    schedules = [schedule_data(s) for s in db.scalars(select(WorkSchedule))]
    latest = db.scalar(select(AttendanceUpload.committed_at).where(AttendanceUpload.committed_at.is_not(None))
                       .order_by(AttendanceUpload.committed_at.desc()).limit(1))
    return {
        "server_time": now,
        "today": today,
        "office_timezone": tz.key,
        "schedule": resolve_schedule(schedules, employee_data(employee), today),
        # The person's own coverage (timesheets that list them); the organisation's latest import otherwise.
        "my_timesheet_through": mine[-1][1] if (mine := coverage(db, [employee.id]).get(employee.id)) else None,
        "timesheet_through": latest_import_through(db),
        "last_import_at": latest,
    }


def timesheet_row(db: Session, employee_id: int, day: date) -> AttendanceDaily | None:
    return db.scalar(select(AttendanceDaily).where(AttendanceDaily.employee_id == employee_id,
                                                   AttendanceDaily.work_date == day))
