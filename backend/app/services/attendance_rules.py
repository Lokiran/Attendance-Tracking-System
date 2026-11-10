"""Pure attendance rules: the single source of truth for day statuses and period metrics.

No database or framework access happens here, so every rule is unit-testable. Callers load
sessions, schedules and holidays, then call :func:`build_day_records` and :func:`summarize`.
Attendance is office presence only: worked hours come from check-in/check-out sessions and are
measured against the shift's required hours. There is no leave.

Rules (also documented in docs/ATTENDANCE_RULES.md):

* A session belongs to the office-local date of its check-in, so overnight sessions count
  fully toward the day they started.
* net minutes of a session = floor((check_out - check_in) in minutes) - break minutes (>= 0).
* Only COMPLETED sessions contribute minutes. OPEN sessions older than ``max_session_minutes``
  and MISSING_CHECKOUT sessions are *unresolved*: they never add minutes and turn a day
  INCOMPLETE unless it is otherwise PRESENT. A younger OPEN session is *active* (e.g. a night
  shift still running after midnight) and makes its start day PRESENT.
* Day status precedence: PRESENT > INCOMPLETE > (scheduled workday: ABSENT /
  NOT_YET_CHECKED_IN) > HOLIDAY / WEEKEND / NOT_SCHEDULED.
* A day is *expected* (in the attendance-percentage denominator) when it is a scheduled
  working day that is not a holiday, has ended or is today with attendance, and is on/after
  the employee's joining date. Today without attendance is NOT_YET_CHECKED_IN and excluded.
* Required minutes of an expected day = the shift's ``required_minutes``; a present past day
  with fewer net minutes gets the SHORT_HOURS exception.
"""

from __future__ import annotations

from collections import defaultdict
from collections.abc import Iterable, Sequence
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, time, timedelta
from zoneinfo import ZoneInfo

from app.models.enums import DayStatus, SessionStatus

EXC_MISSING_CHECKOUT = "MISSING_CHECKOUT"
EXC_LATE_ARRIVAL = "LATE_ARRIVAL"
EXC_SHORT_HOURS = "SHORT_HOURS"
EXC_BELOW_MINIMUM = "BELOW_MINIMUM"
EXC_ABSENT = "ABSENT"


@dataclass(frozen=True)
class PolicyData:
    min_present_minutes: int = 0
    count_incomplete_as_present: bool = False
    max_session_minutes: int = 960
    late_grace_minutes: int = 15


@dataclass(frozen=True)
class SessionData:
    id: int
    employee_id: int
    check_in_at: datetime
    check_out_at: datetime | None
    break_minutes: int
    net_minutes: int | None
    status: SessionStatus


@dataclass(frozen=True)
class ScheduleData:
    id: int
    employee_id: int | None
    department_id: int | None
    start_time: time
    end_time: time
    required_minutes: int
    working_days: frozenset[int]  # ISO weekday 1=Mon..7=Sun
    effective_from: date
    effective_to: date | None
    name: str = ""

    def applies_on(self, d: date) -> bool:
        return self.effective_from <= d and (self.effective_to is None or d <= self.effective_to)


@dataclass(frozen=True)
class HolidayData:
    holiday_date: date
    name: str
    department_id: int | None


@dataclass(frozen=True)
class EmployeeData:
    id: int
    department_id: int | None
    date_joined: date


@dataclass(slots=True)
class DayRecord:
    """One employee-day. Shared between requests by the record cache: treat as read-only."""

    employee_id: int
    date: date
    status: DayStatus
    expected: bool
    scheduled_workday: bool
    required_minutes: int
    net_minutes: int
    completed_sessions: int
    session_count: int
    first_check_in: datetime | None
    last_check_out: datetime | None
    has_active_session: bool
    unresolved_sessions: int
    holiday_name: str | None = None
    exceptions: list[str] = field(default_factory=list)


@dataclass
class Summary:
    start: date
    end: date
    expected_days: int = 0
    present_days: int = 0
    present_expected_days: int = 0
    extra_present_days: int = 0
    absent_days: int = 0
    holiday_days: int = 0
    weekend_days: int = 0
    not_scheduled_days: int = 0
    incomplete_days: int = 0
    not_yet_checked_in_days: int = 0
    net_minutes: int = 0
    required_minutes: int = 0
    attended_days_for_average: int = 0
    unresolved_sessions: int = 0
    late_days: int = 0

    @property
    def variance_minutes(self) -> int:
        return self.net_minutes - self.required_minutes

    @property
    def average_net_minutes(self) -> int | None:
        if self.attended_days_for_average == 0:
            return None
        return round(self.net_minutes / self.attended_days_for_average)

    @property
    def attendance_percentage(self) -> float | None:
        if self.expected_days == 0:
            return None
        return round(self.present_expected_days * 100 / self.expected_days, 1)


class InvalidSessionTimes(ValueError):
    pass


def session_net_minutes(check_in_at: datetime, check_out_at: datetime, break_minutes: int = 0) -> int:
    """Net minutes for a finished session, in whole minutes. Raises on impossible input."""
    if check_in_at.tzinfo is None or check_out_at.tzinfo is None:
        raise InvalidSessionTimes("Timestamps must be timezone-aware")
    if check_out_at <= check_in_at:
        raise InvalidSessionTimes("Check-out must be after check-in")
    if break_minutes < 0:
        raise InvalidSessionTimes("Break minutes cannot be negative")
    gross = int((check_out_at - check_in_at).total_seconds() // 60)
    if break_minutes > gross:
        raise InvalidSessionTimes("Break cannot be longer than the session")
    return gross - break_minutes


def is_unresolved(s: SessionData, today: date, now: datetime, tz: ZoneInfo, policy: PolicyData) -> bool:
    if s.status == SessionStatus.MISSING_CHECKOUT:
        return True
    if s.status != SessionStatus.OPEN:
        return False
    # Age, not calendar day, decides: an overnight shift that is still running is not "missing".
    return (now - s.check_in_at) > timedelta(minutes=policy.max_session_minutes)


def resolve_schedule(schedules: Sequence[ScheduleData], employee: EmployeeData, d: date) -> ScheduleData | None:
    best: tuple[int, date, int] | None = None
    chosen: ScheduleData | None = None
    for s in schedules:
        if not s.applies_on(d):
            continue
        if s.employee_id is not None:
            if s.employee_id != employee.id:
                continue
            tier = 3
        elif s.department_id is not None:
            if s.department_id != employee.department_id:
                continue
            tier = 2
        else:
            tier = 1
        key = (tier, s.effective_from, s.id)
        if best is None or key > best:
            best, chosen = key, s
    return chosen


def build_day_records(
    employee: EmployeeData,
    start: date,
    end: date,
    *,
    today: date,
    now: datetime,
    tz: ZoneInfo,
    sessions: Iterable[SessionData],
    schedules: Sequence[ScheduleData],
    holidays: Iterable[HolidayData],
    policy: PolicyData,
    shared: dict | None = None,
) -> list[DayRecord]:
    """Day records for ``start..min(end, today)``. Future dates are never evaluated.

    ``shared`` is an optional memo that callers computing many employees with the same schedules,
    tz and policy can pass to every call, so schedule lookups and shift start times are worked out
    once per department and day instead of once per person and day.
    """
    end = min(end, today)
    if end < start:
        return []

    by_day: dict[date, list[SessionData]] = defaultdict(list)
    for s in sessions:
        if s.employee_id == employee.id and s.status != SessionStatus.VOIDED:
            by_day[s.check_in_at.astimezone(tz).date()].append(s)
    for day_sessions in by_day.values():
        if len(day_sessions) > 1:
            day_sessions.sort(key=lambda s: s.check_in_at)

    holiday_map: dict[date, str] = {}
    for h in holidays:
        if h.department_id is None or h.department_id == employee.department_id:
            holiday_map.setdefault(h.holiday_date, h.name)

    memo: dict = {} if shared is None else shared
    personal = any(s.employee_id == employee.id for s in schedules)
    ctx = _Ctx(employee, today, now, tz, policy, holiday_map, schedules, memo,
               ("emp", employee.id) if personal else ("dept", employee.department_id))
    records: list[DayRecord] = []
    one_day = timedelta(days=1)
    d = start
    while d <= end:
        records.append(_day_record(ctx, d, by_day.get(d, ())))
        d += one_day
    return records


@dataclass(slots=True)
class _Ctx:
    employee: EmployeeData
    today: date
    now: datetime
    tz: ZoneInfo
    policy: PolicyData
    holiday_map: dict[date, str]
    schedules: Sequence[ScheduleData]
    memo: dict
    schedule_key: tuple

    def schedule_on(self, d: date) -> ScheduleData | None:
        key = (self.schedule_key, d)
        try:
            return self.memo[key]
        except KeyError:
            found = self.memo[key] = resolve_schedule(self.schedules, self.employee, d)
            return found

    def late_after(self, schedule: ScheduleData, d: date) -> datetime:
        key = ("late", schedule.id, d)
        try:
            return self.memo[key]
        except KeyError:
            start_utc = datetime.combine(d, schedule.start_time, tzinfo=self.tz).astimezone(UTC)
            found = self.memo[key] = start_utc + timedelta(minutes=self.policy.late_grace_minutes)
            return found


def _day_record(ctx: _Ctx, d: date, day_sessions: Sequence[SessionData]) -> DayRecord:
    employee, today, policy = ctx.employee, ctx.today, ctx.policy
    schedule = ctx.schedule_on(d) if d >= employee.date_joined else None
    workday = schedule is not None and d.isoweekday() in schedule.working_days
    holiday = ctx.holiday_map.get(d) if workday else None
    holiday_name = ctx.holiday_map.get(d) if schedule is not None else None

    if not day_sessions:  # fast path: no attendance on this day
        if schedule is None:
            status, expected, exceptions = DayStatus.NOT_SCHEDULED, False, []
        elif workday and holiday is None:
            if d == today:
                status, expected, exceptions = DayStatus.NOT_YET_CHECKED_IN, False, []
            else:
                status, expected, exceptions = DayStatus.ABSENT, True, [EXC_ABSENT]
        elif holiday is not None:
            status, expected, exceptions = DayStatus.HOLIDAY, False, []
        else:
            status, expected, exceptions = DayStatus.WEEKEND, False, []
        return DayRecord(employee.id, d, status, expected, workday,
                         schedule.required_minutes if expected else 0, 0, 0, 0, None, None, False, 0,
                         holiday_name, exceptions)

    completed = net = unresolved = active = 0
    last_out: datetime | None = None
    for s in day_sessions:
        status_ = s.status
        if status_ == SessionStatus.COMPLETED:
            completed += 1
            net += s.net_minutes or 0
            if s.check_out_at is not None and (last_out is None or s.check_out_at > last_out):
                last_out = s.check_out_at
        elif is_unresolved(s, today, ctx.now, ctx.tz, policy):
            unresolved += 1
        elif status_ == SessionStatus.OPEN:
            active += 1

    if d < today:
        # A still-running (e.g. overnight) session also makes its start day present.
        qualifies = (completed > 0 and net >= policy.min_present_minutes) or active > 0
    else:  # today is still in progress: any completed or running session counts so far
        qualifies = completed > 0 or active > 0
    present = qualifies or (unresolved > 0 and policy.count_incomplete_as_present)

    exceptions: list[str] = []
    if d < employee.date_joined:
        status, expected = DayStatus.NOT_SCHEDULED, False
    elif present:
        status = DayStatus.PRESENT
        expected = workday and holiday is None
    elif unresolved:
        status = DayStatus.INCOMPLETE
        expected = workday and holiday is None
    elif workday and holiday is None:
        if d == today:
            status, expected = DayStatus.NOT_YET_CHECKED_IN, False
        else:
            status, expected = DayStatus.ABSENT, True
            exceptions.append(EXC_BELOW_MINIMUM if completed else EXC_ABSENT)
    elif holiday is not None:
        status, expected = DayStatus.HOLIDAY, False
    elif schedule is not None:
        status, expected = DayStatus.WEEKEND, False
    else:
        status, expected = DayStatus.NOT_SCHEDULED, False

    if unresolved:
        exceptions.insert(0, EXC_MISSING_CHECKOUT)

    first_in = day_sessions[0].check_in_at
    required = schedule.required_minutes if (expected and schedule is not None) else 0
    if expected and schedule is not None and first_in > ctx.late_after(schedule, d):
        exceptions.append(EXC_LATE_ARRIVAL)
    if expected and status == DayStatus.PRESENT and d < today and completed and net < required:
        exceptions.append(EXC_SHORT_HOURS)

    return DayRecord(employee.id, d, status, expected, workday, required, net, completed, len(day_sessions),
                     first_in, last_out, active > 0, unresolved, holiday_name, exceptions)


def summarize(records: Iterable[DayRecord], start: date, end: date) -> Summary:
    s = Summary(start=start, end=end)
    for r in records:
        s.net_minutes += r.net_minutes
        s.required_minutes += r.required_minutes
        s.unresolved_sessions += r.unresolved_sessions
        if r.expected:
            s.expected_days += 1
        if EXC_LATE_ARRIVAL in r.exceptions:
            s.late_days += 1
        match r.status:
            case DayStatus.PRESENT:
                s.present_days += 1
                if r.expected:
                    s.present_expected_days += 1
                else:
                    s.extra_present_days += 1
                if r.net_minutes > 0:
                    s.attended_days_for_average += 1
            case DayStatus.ABSENT:
                s.absent_days += 1
            case DayStatus.HOLIDAY:
                s.holiday_days += 1
            case DayStatus.WEEKEND:
                s.weekend_days += 1
            case DayStatus.NOT_SCHEDULED:
                s.not_scheduled_days += 1
            case DayStatus.INCOMPLETE:
                s.incomplete_days += 1
            case DayStatus.NOT_YET_CHECKED_IN:
                s.not_yet_checked_in_days += 1
    return s
