"""Pure WFO rules: day classification (Phase 1) and policy compliance (Phase 2). No database access.

Classification of one employee-day, in priority order:

0. An Admin override always wins (source MANUAL_OVERRIDE).
1. The work mode / location column of an imported file (UPLOAD_COLUMN).
2. Badge / swipe / office-access rows of an imported file mean OFFICE (BADGE).
3. Office Wi-Fi from automatic attendance means OFFICE (WIFI).
4. Office or WFH picked by the employee at check-in (CHECK_IN, lower priority than the systems above).
5. Otherwise, worked hours with no office signal mean WFH (INFERRED).
6. No work at all: HOLIDAY or WEEKOFF from the calendar (CALENDAR), or no work mode (absent).

Conflicts: any office signal together with a WFH (or leave / holiday / weekly off) statement makes the
day OFFICE, and each disagreement is written to ``conflicts``.

Policy (all numbers from settings):

* A **WFO day** is a day whose work mode is OFFICE with at least ``min_office_minutes`` office minutes.
* **Working days** (the denominator) are scheduled working days that are not holidays and not LEAVE,
  HOLIDAY or WEEKOFF in the data, plus days actually worked (OFFICE / WFH) on an off day.
* Only **completed** days count (before today). Today is shown, and counted from tomorrow.
* Requirements are prorated, rounding half up: a month needs
  ``min_per_month * working_days / standard_days`` WFO days (standard days = scheduled non-holiday days of
  the whole month), and a week needs ``min_per_week * working_days_in_week / scheduled_days_in_full_week``.
  Weeks are judged on all seven days, including days in the neighbouring month.
  So leave, holidays and the partial weeks at either end of a month lower the requirement fairly, and
  working on an off day or a holiday raises the percentage but never the requirement.
* COMPLIANT when the monthly requirement and every completed week's requirement are met.
  ``shortfall_days`` is the larger of the monthly shortfall and the sum of weekly shortfalls.
"""

from __future__ import annotations

from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass, field
from datetime import date, timedelta
from decimal import ROUND_HALF_UP, Decimal

from app.models.enums import DailyStatus, WfoSource, WorkMode

NOT_WORKING = frozenset({WorkMode.LEAVE, WorkMode.HOLIDAY, WorkMode.WEEKOFF})


# ------------------------------------------------------------------ Phase 1: classification

@dataclass(frozen=True)
class DayEvidence:
    """Everything known about one employee-day. ``None`` means "this source says nothing"."""
    scheduled_workday: bool = True        # the employee's schedule has work that day
    holiday: bool = False                 # a holiday applies to the employee
    override: WorkMode | None = None      # Admin override
    upload_mode: WorkMode | None = None   # work mode column of an imported file
    upload_minutes: int | None = None     # hours column of that row
    badge_minutes: int | None = None      # not None: badge / swipe rows exist (span in minutes, 0 if unknown)
    wifi_minutes: int | None = None       # office Wi-Fi minutes (automatic attendance)
    checkin_office_minutes: int = 0       # completed check-in minutes tagged Office
    checkin_wfh_minutes: int = 0          # ... tagged WFH
    checkin_untagged_minutes: int = 0     # ... without a location
    checkin_office_seen: bool = False     # any check-in tagged Office (also running / unresolved ones)
    checkin_wfh_seen: bool = False


@dataclass(frozen=True)
class DayClass:
    work_mode: WorkMode | None
    source: WfoSource | None
    office_minutes: int
    wfh_minutes: int
    conflicts: tuple[str, ...] = ()


def _office_signals(e: DayEvidence) -> list[tuple[WfoSource, int]]:
    """Office signals with their minutes, highest priority first."""
    signals: list[tuple[WfoSource, int]] = []
    if e.upload_mode == WorkMode.OFFICE:
        signals.append((WfoSource.UPLOAD_COLUMN, e.upload_minutes or 0))
    if e.badge_minutes is not None:
        signals.append((WfoSource.BADGE, e.badge_minutes))
    if e.wifi_minutes:
        signals.append((WfoSource.WIFI, e.wifi_minutes))
    if e.checkin_office_seen or e.checkin_office_minutes:
        signals.append((WfoSource.CHECK_IN, e.checkin_office_minutes))
    return signals


def _office_minutes(signals: Sequence[tuple[WfoSource, int]]) -> int:
    # Sources describe the same time in the office, so take the longest, never the sum.
    return max((m for _s, m in signals), default=0)


def _wfh_minutes(e: DayEvidence) -> int:
    from_checkins = e.checkin_wfh_minutes + e.checkin_untagged_minutes
    from_upload = (e.upload_minutes or 0) if e.upload_mode == WorkMode.WFH else 0
    return max(from_checkins, from_upload)


def classify_day(e: DayEvidence) -> DayClass:
    office = _office_signals(e)
    if e.override is not None:
        # The Admin states where the day was worked, so all of its worked time belongs to that mode.
        worked = max(_office_minutes(office), e.checkin_office_minutes + e.checkin_wfh_minutes
                     + e.checkin_untagged_minutes, e.upload_minutes or 0)
        return DayClass(e.override, WfoSource.MANUAL_OVERRIDE, worked if e.override == WorkMode.OFFICE else 0,
                        worked if e.override == WorkMode.WFH else 0)

    if office:
        source = office[0][0]
        conflicts: list[str] = []
        if e.upload_mode is not None and e.upload_mode != WorkMode.OFFICE:
            conflicts.append(f"UPLOAD_COLUMN said {e.upload_mode.value}; office signal from {source.value}")
        if e.checkin_wfh_seen or e.checkin_wfh_minutes:
            conflicts.append(f"CHECK_IN said WFH; office signal from {source.value}")
        return DayClass(WorkMode.OFFICE, source, _office_minutes(office), e.checkin_wfh_minutes, tuple(conflicts))

    if e.upload_mode in NOT_WORKING:
        conflicts = []
        worked = _wfh_minutes(e) if e.upload_mode != WorkMode.WFH else 0
        if worked:
            conflicts.append(f"UPLOAD_COLUMN said {e.upload_mode.value} but {worked} minutes were checked in")
        return DayClass(e.upload_mode, WfoSource.UPLOAD_COLUMN, 0, 0, tuple(conflicts))
    if e.upload_mode == WorkMode.WFH:
        return DayClass(WorkMode.WFH, WfoSource.UPLOAD_COLUMN, 0, _wfh_minutes(e))
    if e.checkin_wfh_seen or e.checkin_wfh_minutes:
        return DayClass(WorkMode.WFH, WfoSource.CHECK_IN, 0, _wfh_minutes(e))
    if e.checkin_untagged_minutes or e.upload_minutes:
        # Hours were worked but nothing places them in the office.
        return DayClass(WorkMode.WFH, WfoSource.INFERRED, 0, max(e.checkin_untagged_minutes, e.upload_minutes or 0))
    if e.holiday:
        return DayClass(WorkMode.HOLIDAY, WfoSource.CALENDAR, 0, 0)
    if not e.scheduled_workday:
        return DayClass(WorkMode.WEEKOFF, WfoSource.CALENDAR, 0, 0)
    return DayClass(None, None, 0, 0)


def day_status(c: DayClass, full_day_minutes: int, half_day_minutes: int) -> DailyStatus:
    """attendance_daily.status for a classified day (how much was worked; where is the work mode)."""
    if c.work_mode == WorkMode.LEAVE:
        return DailyStatus.LEAVE
    if c.work_mode == WorkMode.HOLIDAY:
        return DailyStatus.HOLIDAY
    if c.work_mode == WorkMode.WEEKOFF:
        return DailyStatus.WEEKEND
    worked = c.office_minutes + c.wfh_minutes
    if worked >= full_day_minutes:
        return DailyStatus.PRESENT
    if worked >= half_day_minutes and worked > 0:
        return DailyStatus.HALF_DAY
    return DailyStatus.ABSENT


# ------------------------------------------------------------------ Phase 2: policy

@dataclass(frozen=True)
class WfoPolicy:
    min_days_per_week: int = 3
    min_days_per_month: int = 12
    min_office_minutes: int = 240


@dataclass(frozen=True)
class PolicyDay:
    """A classified day as the policy sees it."""
    date: date
    work_mode: WorkMode | None
    office_minutes: int
    scheduled: bool          # scheduled working day per schedule, not a holiday


def is_wfo_day(d: PolicyDay, policy: WfoPolicy) -> bool:
    return d.work_mode == WorkMode.OFFICE and d.office_minutes >= policy.min_office_minutes


def is_working_day(d: PolicyDay) -> bool:
    if d.work_mode in NOT_WORKING:
        return False
    return d.scheduled or d.work_mode in (WorkMode.OFFICE, WorkMode.WFH)


def is_scheduled_working_day(d: PolicyDay) -> bool:
    """Working days that sized the requirement: scheduled, and not leave / holiday / weekly off. Extra days
    worked on an off day count for the percentage but never raise the requirement."""
    return d.scheduled and d.work_mode not in NOT_WORKING


def round_half_up(x: float) -> int:
    return int(Decimal(str(x)).quantize(Decimal("1"), rounding=ROUND_HALF_UP))


@dataclass
class WeekResult:
    week_start: date            # Monday
    week_end: date              # Sunday
    label: str
    working_days: int
    wfo_days: int
    required: int
    status: str                 # MET | SHORT | IN_PROGRESS (contains today) | UPCOMING (not started)
    shortfall: int


@dataclass
class WfoSummary:
    working_days: int = 0
    standard_days: int = 0
    wfo_days: int = 0
    wfh_days: int = 0
    office_days_below_minimum: int = 0
    leave_days: int = 0
    holiday_days: int = 0
    weekoff_days: int = 0
    required_days: int = 0
    shortfall_days: int = 0
    wfo_percentage: float | None = None
    compliance: str = "COMPLIANT"
    weeks: list[WeekResult] = field(default_factory=list)


def summarize_wfo(days: Iterable[PolicyDay], *, start: date, end: date, today: date, policy: WfoPolicy,
                  is_scheduled: Callable[[date], bool]) -> WfoSummary:
    """Policy figures for ``start..end``. ``days`` may include today and future days; only days before
    ``today`` are counted. Supply the days of the first and last week outside the range too (six days
    either side): weeks are judged on all their days; monthly figures use only ``start..end``.
    ``is_scheduled(d)`` tells whether the schedule has work on d (ignoring
    holidays), for any date: it sizes the nominal week and the standard month."""
    by_date = {d.date: d for d in days}
    s = WfoSummary()
    # Standard days: scheduled non-holiday days of the whole period (the full month, future included).
    s.standard_days = sum(1 for d in _dates(start, end) if (by_date[d].scheduled if d in by_date else False))
    completed = [by_date[d] for d in _dates(start, min(end, today - timedelta(days=1))) if d in by_date]
    scheduled_working = sum(1 for d in completed if is_scheduled_working_day(d))
    for d in completed:
        if is_working_day(d):
            s.working_days += 1
        if is_wfo_day(d, policy):
            s.wfo_days += 1
        elif d.work_mode == WorkMode.OFFICE:
            s.office_days_below_minimum += 1
        if d.work_mode == WorkMode.WFH:
            s.wfh_days += 1
        elif d.work_mode == WorkMode.LEAVE:
            s.leave_days += 1
        elif d.work_mode == WorkMode.HOLIDAY:
            s.holiday_days += 1
        elif d.work_mode == WorkMode.WEEKOFF:
            s.weekoff_days += 1

    if s.standard_days:
        s.required_days = min(scheduled_working,
                              round_half_up(policy.min_days_per_month * scheduled_working / s.standard_days))
    monthly_short = max(0, s.required_days - s.wfo_days)

    week_short_total = 0
    week = start - timedelta(days=start.weekday())
    while week <= end:
        week_end = week + timedelta(days=6)
        # A week is judged on all seven days, also those in the neighbouring month (when supplied), so a
        # week that straddles two months gets the same verdict in both.
        counted = [by_date[d] for d in _dates(week, week_end) if d < today and d in by_date]
        working = sum(1 for d in counted if is_working_day(d))
        sized = sum(1 for d in counted if is_scheduled_working_day(d))
        wfo = sum(1 for d in counted if is_wfo_day(d, policy))
        nominal = sum(1 for d in _dates(week, week_end) if is_scheduled(d))
        required = min(sized, round_half_up(policy.min_days_per_week * sized / nominal)) if nominal else 0
        upcoming = week > today                      # nothing of this week has happened yet
        in_progress = not upcoming and week_end >= today
        shortfall = 0 if (in_progress or upcoming) else max(0, required - wfo)
        week_short_total += shortfall
        status = "UPCOMING" if upcoming else "IN_PROGRESS" if in_progress else ("SHORT" if shortfall else "MET")
        s.weeks.append(WeekResult(week, week_end, f"{week:%d %b} – {week_end:%d %b}", working, wfo, required,
                                  status, shortfall))
        week += timedelta(days=7)

    s.shortfall_days = max(monthly_short, week_short_total)
    s.compliance = "NON_COMPLIANT" if (monthly_short or week_short_total) else "COMPLIANT"
    s.wfo_percentage = round(s.wfo_days * 100 / s.working_days, 1) if s.working_days else None
    return s


def _dates(start: date, end: date):
    d = start
    while d <= end:
        yield d
        d += timedelta(days=1)
