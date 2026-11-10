"""Pure attendance rules: the single source of truth for day statuses and period metrics.

No database or framework access happens here, so every rule is unit-testable. Callers load timesheet
days, schedules, holidays and the timesheet coverage, then call :func:`build_day_records` and
:func:`summarize`.

Attendance is **office presence from the timesheets the Admin imports** (there is no check-in and no
leave request). Rules (also documented in docs/ATTENDANCE_RULES.md):

* A timesheet day says where a day was worked (OFFICE / WFH / LEAVE / HOLIDAY / WEEKOFF) and, when the
  file has them, office hours and in / out times.
* OFFICE -> PRESENT. When the timesheet has hours they are the day's worked minutes; when it has none the
  day is still PRESENT, hours are "not recorded" (``hours_known`` False) and no short-hours exception is
  raised. With hours below the policy's ``min_present_minutes`` the day is ABSENT (BELOW_MINIMUM).
* WFH -> WFH: a working day, but not office attendance (it counts in the denominator, not as present).
* LEAVE / HOLIDAY / WEEKOFF from the timesheet -> LEAVE / HOLIDAY / WEEKEND, not expected.
* No timesheet entry on a scheduled working day that is not a holiday: ABSENT when a committed timesheet
  covers that date, otherwise NO_TIMESHEET (not expected): a month whose timesheet has not been imported
  yet is not a month of absences.
* A day is *expected* (in the attendance-percentage denominator) when it is a scheduled working day that
  is not a holiday, on/after the employee's joining date, and is PRESENT, WFH or ABSENT.
* Required minutes of an expected day = the shift's ``required_minutes``. A present past day with recorded
  hours below that gets SHORT_HOURS; an in time later than shift start + grace gets LATE_ARRIVAL.
* Hours totals are compared only over days whose hours are recorded (``hours_days``,
  ``hours_required_minutes``), so a presence list without hours never reads as hours short.
"""

from __future__ import annotations

from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, time, timedelta
from zoneinfo import ZoneInfo

from app.models.enums import DayStatus, WorkMode

EXC_LATE_ARRIVAL = "LATE_ARRIVAL"
EXC_SHORT_HOURS = "SHORT_HOURS"
EXC_BELOW_MINIMUM = "BELOW_MINIMUM"
EXC_ABSENT = "ABSENT"


@dataclass(frozen=True)
class PolicyData:
    min_present_minutes: int = 0
    late_grace_minutes: int = 15


@dataclass(frozen=True)
class TimesheetDay:
    """What the imported timesheets (and Admin overrides) say about one employee-day."""
    employee_id: int
    date: date
    work_mode: WorkMode | None
    minutes: int | None = None          # office minutes of an OFFICE day; None: not recorded
    first_in: datetime | None = None
    last_out: datetime | None = None


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
    net_minutes: int                    # office minutes from the timesheet (0 when not recorded)
    hours_known: bool                   # the timesheet gave hours (or in / out times) for this day
    work_mode: WorkMode | None
    first_in: datetime | None
    last_out: datetime | None
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
    wfh_days: int = 0
    leave_days: int = 0
    holiday_days: int = 0
    weekend_days: int = 0
    not_scheduled_days: int = 0
    no_timesheet_days: int = 0
    net_minutes: int = 0
    required_minutes: int = 0
    hours_days: int = 0                 # present days with recorded hours
    hours_required_minutes: int = 0     # shift minutes of those days
    late_days: int = 0

    @property
    def variance_minutes(self) -> int:
        """Recorded office minutes against the shift minutes of the same days."""
        return self.net_minutes - self.hours_required_minutes

    @property
    def average_net_minutes(self) -> int | None:
        if self.hours_days == 0:
            return None
        return round(self.net_minutes / self.hours_days)

    @property
    def attendance_percentage(self) -> float | None:
        if self.expected_days == 0:
            return None
        return round(self.present_expected_days * 100 / self.expected_days, 1)


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


def _everything(_d: date) -> bool:
    return True


def build_day_records(
    employee: EmployeeData,
    start: date,
    end: date,
    *,
    today: date,
    tz: ZoneInfo,
    timesheet: Iterable[TimesheetDay],
    schedules: Sequence[ScheduleData],
    holidays: Iterable[HolidayData],
    policy: PolicyData,
    covered: Callable[[date], bool] = _everything,
    shared: dict | None = None,
) -> list[DayRecord]:
    """Day records for ``start..min(end, today)``. Future dates are never evaluated.

    ``covered(d)`` says whether a committed timesheet covers date ``d`` (a scheduled day missing from a
    covering timesheet is absent; otherwise it has no timesheet yet). ``shared`` is an optional memo that
    callers computing many employees with the same schedules, tz and policy pass to every call, so schedule
    lookups and shift start times are worked out once per department and day.
    """
    end = min(end, today)
    if end < start:
        return []
    by_day = {t.date: t for t in timesheet if t.employee_id == employee.id}
    holiday_map: dict[date, str] = {}
    for h in holidays:
        if h.department_id is None or h.department_id == employee.department_id:
            holiday_map.setdefault(h.holiday_date, h.name)
    memo: dict = {} if shared is None else shared
    personal = any(s.employee_id == employee.id for s in schedules)
    ctx = _Ctx(employee, today, tz, policy, holiday_map, schedules, memo, covered,
               ("emp", employee.id) if personal else ("dept", employee.department_id))
    records: list[DayRecord] = []
    one_day = timedelta(days=1)
    d = start
    while d <= end:
        records.append(_day_record(ctx, d, by_day.get(d)))
        d += one_day
    return records


@dataclass(slots=True)
class _Ctx:
    employee: EmployeeData
    today: date
    tz: ZoneInfo
    policy: PolicyData
    holiday_map: dict[date, str]
    schedules: Sequence[ScheduleData]
    memo: dict
    covered: Callable[[date], bool]
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


def _day_record(ctx: _Ctx, d: date, ts: TimesheetDay | None) -> DayRecord:
    employee, today, policy = ctx.employee, ctx.today, ctx.policy
    schedule = ctx.schedule_on(d) if d >= employee.date_joined else None
    workday = schedule is not None and d.isoweekday() in schedule.working_days
    holiday = ctx.holiday_map.get(d) if workday else None
    holiday_name = ctx.holiday_map.get(d) if schedule is not None else None
    working = workday and holiday is None
    required = schedule.required_minutes if (working and schedule is not None) else 0
    mode = ts.work_mode if ts is not None and d >= employee.date_joined else None

    if mode is None:
        if d < employee.date_joined or schedule is None:
            status, expected, exceptions = DayStatus.NOT_SCHEDULED, False, []
        elif working:
            if d < today and ctx.covered(d):
                status, expected, exceptions = DayStatus.ABSENT, True, [EXC_ABSENT]
            else:
                status, expected, exceptions = DayStatus.NO_TIMESHEET, False, []
        elif holiday is not None:
            status, expected, exceptions = DayStatus.HOLIDAY, False, []
        else:
            status, expected, exceptions = DayStatus.WEEKEND, False, []
        return DayRecord(employee.id, d, status, expected, workday, required if expected else 0, 0, False, None,
                         None, None, holiday_name, exceptions)

    exceptions: list[str] = []
    net, hours_known = 0, False
    if mode == WorkMode.OFFICE:
        hours_known = ts.minutes is not None
        net = ts.minutes or 0
        if hours_known and d < today and policy.min_present_minutes and net < policy.min_present_minutes:
            status, expected = DayStatus.ABSENT, working
            exceptions.append(EXC_BELOW_MINIMUM)
        else:
            status, expected = DayStatus.PRESENT, working
            if expected and schedule is not None:
                if ts.first_in is not None and ts.first_in > ctx.late_after(schedule, d):
                    exceptions.append(EXC_LATE_ARRIVAL)
                if hours_known and d < today and net < required:
                    exceptions.append(EXC_SHORT_HOURS)
    elif mode == WorkMode.WFH:
        status, expected = DayStatus.WFH, working
    elif mode == WorkMode.LEAVE:
        status, expected = DayStatus.LEAVE, False
    elif mode == WorkMode.HOLIDAY:
        status, expected = DayStatus.HOLIDAY, False
    else:  # WEEKOFF
        status, expected = DayStatus.WEEKEND, False

    return DayRecord(employee.id, d, status, expected, workday, required if expected else 0, net, hours_known,
                     mode, ts.first_in, ts.last_out, holiday_name, exceptions)


def summarize(records: Iterable[DayRecord], start: date, end: date) -> Summary:
    s = Summary(start=start, end=end)
    for r in records:
        s.net_minutes += r.net_minutes
        s.required_minutes += r.required_minutes
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
                if r.hours_known:
                    s.hours_days += 1
                    s.hours_required_minutes += r.required_minutes
            case DayStatus.ABSENT:
                s.absent_days += 1
            case DayStatus.WFH:
                s.wfh_days += 1
            case DayStatus.LEAVE:
                s.leave_days += 1
            case DayStatus.HOLIDAY:
                s.holiday_days += 1
            case DayStatus.WEEKEND:
                s.weekend_days += 1
            case DayStatus.NOT_SCHEDULED:
                s.not_scheduled_days += 1
            case DayStatus.NO_TIMESHEET:
                s.no_timesheet_days += 1
    return s
