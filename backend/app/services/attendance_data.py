"""Loads attendance inputs from the database in bulk and feeds them to the pure rules engine.

Scale notes (1 admin, a few managers, thousands of employees):

* Sessions are read as plain column rows, not ORM objects, and large scopes are read with one
  range query instead of thousands of ``IN`` parameters.
* A dashboard page sends several requests (cards, table, charts) for the same scope and dates.
  :func:`compute_records` computes the day records once and lets the other requests reuse them
  through :mod:`app.services.record_cache`. The cache key contains everything the result depends
  on (people, dates, the minute, schedules, holidays, policy and a fingerprint of the sessions in
  range), so any write is visible on the next request, from any server process.
"""

import gc
from collections import defaultdict
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import date, datetime, timedelta

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.timeutil import local_date, local_day_bounds, office_tz
from app.models import (
    AttendanceSession,
    Holiday,
    SessionStatus,
    User,
    WorkSchedule,
)
from app.services import record_cache
from app.services.attendance_rules import (
    DayRecord,
    EmployeeData,
    HolidayData,
    PolicyData,
    ScheduleData,
    SessionData,
    build_day_records,
)
from app.services.policy import get_policy, policy_data

_CHUNK = 500
# Above this many people one range query (filtered in Python) beats many IN (...) chunks.
_RANGE_SCAN_MIN = 2000
# Scopes smaller than this are cheap to compute and are not cached.
CACHE_MIN_EMPLOYEES = 25


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
    policy: PolicyData


_SESSION_COLUMNS = (
    AttendanceSession.id, AttendanceSession.employee_id, AttendanceSession.check_in_at,
    AttendanceSession.check_out_at, AttendanceSession.break_minutes, AttendanceSession.net_minutes,
    AttendanceSession.status,
)


def _load_sessions(db: Session, employee_ids: list[int], start: date, end: date) -> dict[int, list[SessionData]]:
    lo, hi = local_day_bounds(start, end)
    in_range = (AttendanceSession.check_in_at >= lo, AttendanceSession.check_in_at < hi,
                AttendanceSession.status != SessionStatus.VOIDED)
    sessions: dict[int, list[SessionData]] = defaultdict(list)
    if len(employee_ids) >= _RANGE_SCAN_MIN:
        wanted = set(employee_ids)
        queries = [select(*_SESSION_COLUMNS).where(*in_range)]
    else:
        wanted = None
        queries = [select(*_SESSION_COLUMNS).where(AttendanceSession.employee_id.in_(employee_ids[i:i + _CHUNK]),
                                                   *in_range)
                   for i in range(0, len(employee_ids), _CHUNK)]
    for q in queries:
        for sid, eid, cin, cout, brk, net, status in db.execute(q):
            if wanted is None or eid in wanted:
                sessions[eid].append(SessionData(sid, eid, cin, cout, brk, net, SessionStatus(status)))
    return sessions


def _load_rules_inputs(db: Session, start: date, end: date) -> tuple[list[ScheduleData], list[HolidayData], PolicyData]:
    schedules = [
        schedule_data(s)
        for s in db.scalars(
            select(WorkSchedule).where(
                WorkSchedule.effective_from <= end,
                (WorkSchedule.effective_to.is_(None)) | (WorkSchedule.effective_to >= start),
            ).order_by(WorkSchedule.id)
        )
    ]
    holidays = [
        HolidayData(h.holiday_date, h.name, h.department_id)
        for h in db.scalars(select(Holiday).where(Holiday.holiday_date.between(start, end)).order_by(Holiday.id))
    ]
    return schedules, holidays, policy_data(get_policy(db))


def load_bundle(db: Session, employee_ids: list[int], start: date, end: date) -> Bundle:
    schedules, holidays, policy = _load_rules_inputs(db, start, end)
    return Bundle(_load_sessions(db, employee_ids, start, end), schedules, holidays, policy)


def _sessions_fingerprint(db: Session, start: date, end: date) -> tuple:
    """Changes whenever a session in the range is added, changed, voided or removed."""
    lo, hi = local_day_bounds(start, end)
    count, newest_change, max_id = db.execute(
        select(func.count(), func.max(AttendanceSession.updated_at), func.max(AttendanceSession.id))
        .where(AttendanceSession.check_in_at >= lo, AttendanceSession.check_in_at < hi)
    ).one()
    return count, newest_change, max_id


CLOCK_BUCKET_MINUTES = 5


def _clock_bucket(now: datetime) -> datetime:
    return now.replace(minute=now.minute - now.minute % CLOCK_BUCKET_MINUTES, second=0, microsecond=0)


@contextmanager
def _gc_paused():
    """Building hundreds of thousands of day records triggers Python's cyclic garbage collector over and
    over, and each run re-scans every live object (including cached results), so the cost grows faster
    than the data. The records contain no reference cycles, so collection can safely wait until the
    build is done. Only the caller that actually turned collection off turns it back on."""
    was_enabled = gc.isenabled()
    gc.disable()
    try:
        yield
    finally:
        if was_enabled:
            gc.enable()


def compute_records(db: Session, employees: list[User], start: date, end: date) -> dict[int, list[DayRecord]]:
    """Day records per employee for the range (clipped to today). Callers must not modify them."""
    now = clock.utcnow()
    tz = office_tz()
    today = local_date(now, tz)
    schedules, holidays, policy = _load_rules_inputs(db, start, end)
    people = [employee_data(e) for e in employees]

    def compute() -> dict[int, list[DayRecord]]:
        sessions = _load_sessions(db, [p.id for p in people], start, end)
        shared: dict = {}  # schedule / shift-start memo reused across everyone in this computation
        with _gc_paused():
            return {
                p.id: build_day_records(p, start, end, today=today, now=now, tz=tz, sessions=sessions.get(p.id, ()),
                                        schedules=schedules, holidays=holidays, policy=policy, shared=shared)
                for p in people
            }

    if len(people) < CACHE_MIN_EMPLOYEES:
        return compute()
    key = (
        tuple(people), start, end, today,
        # Without any write, results change with the clock only when a running session passes the
        # maximum session length (it becomes "missing check-out"), and only for ranges reaching
        # today or yesterday. Re-checking that every few minutes is plenty; writes are seen at once.
        _clock_bucket(now) if end >= today - timedelta(days=1) else None,
        tuple(schedules), tuple(holidays),
        policy, _sessions_fingerprint(db, start, end), record_cache.write_version(),
    )
    size = len(people) * ((min(end, today) - start).days + 1)
    return record_cache.get_or_compute(key, size, compute)
