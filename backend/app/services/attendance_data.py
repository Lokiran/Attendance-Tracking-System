"""Loads attendance inputs from the database in bulk and feeds them to the pure rules engine."""

from collections import defaultdict
from dataclasses import dataclass
from datetime import date

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.timeutil import local_date, local_day_bounds, office_tz
from app.models import (
    AttendanceSession,
    Holiday,
    LeaveRecord,
    RequestStatus,
    SessionStatus,
    User,
    WorkSchedule,
)
from app.services.attendance_rules import (
    DayRecord,
    EmployeeData,
    HolidayData,
    LeaveData,
    PolicyData,
    ScheduleData,
    SessionData,
    build_day_records,
)
from app.services.policy import get_policy, policy_data

_CHUNK = 500


def session_data(s: AttendanceSession) -> SessionData:
    return SessionData(
        id=s.id, employee_id=s.employee_id, check_in_at=s.check_in_at, check_out_at=s.check_out_at,
        break_minutes=s.break_minutes, net_minutes=s.net_minutes, status=SessionStatus(s.status),
    )


def schedule_data(s: WorkSchedule) -> ScheduleData:
    return ScheduleData(
        id=s.id, employee_id=s.employee_id, department_id=s.department_id, start_time=s.start_time,
        end_time=s.end_time, required_minutes=s.required_minutes, working_days=frozenset(s.working_days),
        effective_from=s.effective_from, effective_to=s.effective_to, name=s.name,
    )


def employee_data(u: User) -> EmployeeData:
    return EmployeeData(id=u.id, department_id=u.department_id, date_joined=u.date_joined)


@dataclass
class Bundle:
    sessions: dict[int, list[SessionData]]
    schedules: list[ScheduleData]
    holidays: list[HolidayData]
    leaves: dict[int, list[LeaveData]]
    policy: PolicyData


def _chunks(ids: list[int]):
    for i in range(0, len(ids), _CHUNK):
        yield ids[i:i + _CHUNK]


def load_bundle(db: Session, employee_ids: list[int], start: date, end: date) -> Bundle:
    lo, hi = local_day_bounds(start, end)
    sessions: dict[int, list[SessionData]] = defaultdict(list)
    leaves: dict[int, list[LeaveData]] = defaultdict(list)
    for chunk in _chunks(employee_ids):
        for s in db.scalars(
            select(AttendanceSession).where(
                AttendanceSession.employee_id.in_(chunk),
                AttendanceSession.check_in_at >= lo,
                AttendanceSession.check_in_at < hi,
                AttendanceSession.status != SessionStatus.VOIDED,
            )
        ):
            sessions[s.employee_id].append(session_data(s))
        for lv in db.scalars(
            select(LeaveRecord).where(
                LeaveRecord.employee_id.in_(chunk),
                LeaveRecord.status == RequestStatus.APPROVED,
                LeaveRecord.start_date <= end,
                LeaveRecord.end_date >= start,
            )
        ).unique():
            leaves[lv.employee_id].append(LeaveData(lv.employee_id, lv.start_date, lv.end_date, lv.leave_type.value))

    schedules = [
        schedule_data(s)
        for s in db.scalars(
            select(WorkSchedule).where(
                WorkSchedule.effective_from <= end,
                (WorkSchedule.effective_to.is_(None)) | (WorkSchedule.effective_to >= start),
            )
        )
    ]
    holidays = [
        HolidayData(h.holiday_date, h.name, h.department_id)
        for h in db.scalars(select(Holiday).where(Holiday.holiday_date.between(start, end)))
    ]
    return Bundle(sessions, schedules, holidays, leaves, policy_data(get_policy(db)))


def compute_records(db: Session, employees: list[User], start: date, end: date) -> dict[int, list[DayRecord]]:
    """Day records per employee for the range (clipped to today)."""
    bundle = load_bundle(db, [e.id for e in employees], start, end)
    now = clock.utcnow()
    tz = office_tz()
    today = local_date(now, tz)
    return {
        e.id: build_day_records(
            employee_data(e), start, end, today=today, now=now, tz=tz,
            sessions=bundle.sessions.get(e.id, []), schedules=bundle.schedules, holidays=bundle.holidays,
            leaves=bundle.leaves.get(e.id, []), policy=bundle.policy,
        )
        for e in employees
    }
