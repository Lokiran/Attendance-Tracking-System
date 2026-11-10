"""Loads attendance inputs from the database in bulk and feeds them to the pure rules engine.

Attendance comes from the imported timesheets: each ``attendance_daily`` row holds what the timesheets
(and Admin overrides) say about an employee-day; :func:`timesheet_day` turns it into the work mode and
office hours with the same priority rules as WFO (``wfo_rules.classify_day``). A committed upload covers its
period **for the people listed in it** (:func:`coverage`): a partial file (one team, one correction) never
makes anyone else absent.

Scale notes (1 admin, a few managers, thousands of employees):

* Timesheet rows are read as plain column rows, not ORM objects, and large scopes are read with one
  range query instead of thousands of ``IN`` parameters.
* A dashboard page sends several requests (cards, table, charts) for the same scope and dates.
  :func:`compute_records` computes the day records once and lets the other requests reuse them
  through :mod:`app.services.record_cache`. The cache key contains everything the result depends
  on (people, dates, schedules, holidays, policy, coverage and a fingerprint of the timesheet rows in
  range), so any write is visible on the next request, from any server process.
"""

import bisect
import gc
from collections import defaultdict
from contextlib import contextmanager
from datetime import date, datetime

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.timeutil import local_date, office_tz
from app.models import (
    AttendanceDaily,
    AttendanceUpload,
    AttendanceUploadRow,
    Holiday,
    UploadStatus,
    User,
    WorkMode,
    WorkSchedule,
)
from app.services import record_cache
from app.services.attendance_rules import (
    DayRecord,
    EmployeeData,
    HolidayData,
    PolicyData,
    ScheduleData,
    TimesheetDay,
    build_day_records,
)
from app.services.policy import get_policy, policy_data
from app.services.wfo_rules import DayEvidence, classify_day

_CHUNK = 500
# Above this many people one range query (filtered in Python) beats many IN (...) chunks.
_RANGE_SCAN_MIN = 2000
# Scopes smaller than this are cheap to compute and are not cached.
CACHE_MIN_EMPLOYEES = 25


def schedule_data(s: WorkSchedule) -> ScheduleData:
    return ScheduleData(
        id=s.id, employee_id=s.employee_id, department_id=s.department_id, start_time=s.start_time,
        end_time=s.end_time, required_minutes=s.required_minutes, working_days=frozenset(s.working_days),
        effective_from=s.effective_from, effective_to=s.effective_to, name=s.name,
    )


def employee_data(u: User) -> EmployeeData:
    return EmployeeData(id=u.id, department_id=u.department_id, date_joined=u.date_joined)


_DAILY_COLUMNS = (
    AttendanceDaily.employee_id, AttendanceDaily.work_date, AttendanceDaily.override_work_mode,
    AttendanceDaily.upload_work_mode, AttendanceDaily.upload_minutes, AttendanceDaily.badge_minutes,
    AttendanceDaily.wifi_minutes, AttendanceDaily.first_in, AttendanceDaily.last_out,
)


def timesheet_day(eid: int, d: date, override, upload_mode, upload_minutes, badge_minutes, wifi_minutes,
                  first_in: datetime | None, last_out: datetime | None) -> TimesheetDay | None:
    """The work mode and office hours one attendance_daily row stands for (None: it says nothing)."""
    if last_out is not None and (first_in is None or last_out < first_in):
        last_out = None
    if override is None and badge_minutes is None and not wifi_minutes:
        # Fast path for the usual row (only the timesheet's own columns), identical to classify_day:
        # an OFFICE row's hours are its hours column; other modes carry no office hours; hours without a
        # mode mean WFH (INFERRED).
        if upload_mode is not None:
            mode = WorkMode(upload_mode)
            return TimesheetDay(eid, d, mode, upload_minutes if mode == WorkMode.OFFICE else None, first_in, last_out)
        if upload_minutes:
            return TimesheetDay(eid, d, WorkMode.WFH, None, first_in, last_out)
        return None
    cls = classify_day(DayEvidence(override=override, upload_mode=upload_mode, upload_minutes=upload_minutes,
                                   badge_minutes=badge_minutes, wifi_minutes=wifi_minutes))
    if cls.work_mode is None:
        return None
    minutes = cls.office_minutes if cls.work_mode == WorkMode.OFFICE and cls.hours_known else None
    return TimesheetDay(eid, d, cls.work_mode, minutes, first_in, last_out)


def load_timesheet(db: Session, employee_ids: list[int], start: date, end: date) -> dict[int, list[TimesheetDay]]:
    in_range = (AttendanceDaily.work_date >= start, AttendanceDaily.work_date <= end)
    out: dict[int, list[TimesheetDay]] = defaultdict(list)
    if len(employee_ids) >= _RANGE_SCAN_MIN:
        wanted = set(employee_ids)
        queries = [select(*_DAILY_COLUMNS).where(*in_range)]
    else:
        wanted = None
        queries = [select(*_DAILY_COLUMNS).where(AttendanceDaily.employee_id.in_(employee_ids[i:i + _CHUNK]),
                                                 *in_range)
                   for i in range(0, len(employee_ids), _CHUNK)]
    conn = db.connection()  # Core rows: no ORM loading overhead for hundreds of thousands of rows
    for q in queries:
        for row in conn.execute(q):
            if wanted is None or row[0] in wanted:
                t = timesheet_day(*row)
                if t is not None:
                    out[row[0]].append(t)
    return out


Periods = tuple[tuple[date, date], ...]


def _merge(periods) -> Periods:
    merged: list[tuple[date, date]] = []
    for a, b in sorted(periods):
        if merged and (a - merged[-1][1]).days <= 1:
            merged[-1] = (merged[-1][0], max(merged[-1][1], b))
        else:
            merged.append((a, b))
    return tuple(merged)


_COMMITTED = (AttendanceUpload.status == UploadStatus.COMMITTED, AttendanceUpload.period_start.is_not(None),
              AttendanceUpload.period_end.is_not(None))


def coverage(db: Session, employee_ids: list[int]) -> dict[int, Periods]:
    """Per employee, the merged periods of the committed timesheets that list them (valid rows only)."""
    found: dict[int, set[tuple[date, date]]] = defaultdict(set)
    base = (select(AttendanceUploadRow.employee_id, AttendanceUpload.period_start, AttendanceUpload.period_end)
            .join(AttendanceUpload, AttendanceUpload.id == AttendanceUploadRow.upload_id)
            .where(*_COMMITTED, AttendanceUploadRow.error.is_(None), AttendanceUploadRow.employee_id.is_not(None))
            .distinct())
    if len(employee_ids) >= _RANGE_SCAN_MIN:
        wanted = set(employee_ids)
        queries = [base]
    else:
        wanted = None
        queries = [base.where(AttendanceUploadRow.employee_id.in_(employee_ids[i:i + _CHUNK]))
                   for i in range(0, len(employee_ids), _CHUNK)]
    for q in queries:
        for eid, a, b in db.execute(q):
            if wanted is None or eid in wanted:
                found[eid].add((a, b))
    return {eid: _merge(periods) for eid, periods in found.items()}


def covered_fn(periods: Periods):
    starts = [a for a, _b in periods]

    def covered(d: date) -> bool:
        i = bisect.bisect_right(starts, d) - 1
        return i >= 0 and d <= periods[i][1]
    return covered


def latest_import_through(db: Session) -> date | None:
    """The last date any committed timesheet covers (for "timesheets imported up to")."""
    return db.scalar(select(func.max(AttendanceUpload.period_end)).where(*_COMMITTED))


def _coverage_fingerprint(db: Session) -> tuple:
    return tuple(db.execute(select(func.count(), func.max(AttendanceUpload.id), func.max(AttendanceUpload.committed_at))
                            .where(*_COMMITTED)).one())


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


def _timesheet_fingerprint(db: Session, start: date, end: date) -> tuple:
    """Changes whenever a timesheet row in the range is added, changed or removed."""
    return tuple(db.execute(
        select(func.count(), func.max(AttendanceDaily.updated_at), func.max(AttendanceDaily.id))
        .where(AttendanceDaily.work_date >= start, AttendanceDaily.work_date <= end)
    ).one())


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
    tz = office_tz()
    today = local_date(clock.utcnow(), tz)
    schedules, holidays, policy = _load_rules_inputs(db, start, end)
    people = [employee_data(e) for e in employees]

    def compute() -> dict[int, list[DayRecord]]:
        ids = [p.id for p in people]
        timesheet = load_timesheet(db, ids, start, end)
        periods = coverage(db, ids)
        shared: dict = {}  # schedule / shift-start memo reused across everyone in this computation
        with _gc_paused():
            return {
                p.id: build_day_records(p, start, end, today=today, tz=tz, timesheet=timesheet.get(p.id, ()),
                                        schedules=schedules, holidays=holidays, policy=policy,
                                        covered=covered_fn(periods.get(p.id, ())), shared=shared)
                for p in people
            }

    if len(people) < CACHE_MIN_EMPLOYEES:
        return compute()
    key = (tuple(people), start, end, today, tuple(schedules), tuple(holidays), policy, _coverage_fingerprint(db),
           _timesheet_fingerprint(db, start, end), record_cache.write_version())
    size = len(people) * ((min(end, today) - start).days + 1)
    return record_cache.get_or_compute(key, size, compute)
