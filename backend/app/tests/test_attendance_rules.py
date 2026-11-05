"""Unit tests for the pure attendance rules engine (no database)."""

from datetime import UTC, date, datetime, time, timedelta
from zoneinfo import ZoneInfo

import pytest

from app.models import DayStatus, SessionStatus
from app.services.attendance_rules import (
    EXC_LATE_ARRIVAL,
    EXC_MISSING_CHECKOUT,
    EXC_SHORT_HOURS,
    EmployeeData,
    HolidayData,
    InvalidSessionTimes,
    LeaveData,
    PolicyData,
    ScheduleData,
    SessionData,
    build_day_records,
    resolve_schedule,
    session_net_minutes,
    summarize,
)

IST = ZoneInfo("Asia/Kolkata")
EMP = EmployeeData(id=1, department_id=10, date_joined=date(2025, 1, 1))
WEEKDAYS = ScheduleData(1, None, None, time(9, 30), time(18, 30), 480, frozenset({1, 2, 3, 4, 5}),
                        date(2024, 1, 1), None)
POLICY = PolicyData()
_ids = iter(range(1, 10_000))


def at(d: date, h: int, m: int = 0) -> datetime:
    return datetime.combine(d, time(h, m), tzinfo=IST).astimezone(UTC)


def done(d: date, h1, m1, h2, m2, brk=0, day2: date | None = None) -> SessionData:
    a, b = at(d, h1, m1), at(day2 or d, h2, m2)
    return SessionData(next(_ids), 1, a, b, brk, session_net_minutes(a, b, brk), SessionStatus.COMPLETED)


def open_s(d: date, h, m=0, status=SessionStatus.OPEN) -> SessionData:
    return SessionData(next(_ids), 1, at(d, h, m), None, 0, None, status)


def records(start, end, sessions=(), *, today=date(2026, 3, 13), now=None, schedules=(WEEKDAYS,), holidays=(),
            leaves=(), policy=POLICY, employee=EMP):
    now = now or at(today, 20)
    return build_day_records(employee, start, end, today=today, now=now, tz=IST, sessions=list(sessions),
                             schedules=list(schedules), holidays=list(holidays), leaves=list(leaves), policy=policy)


MON, TUE, WED, THU, FRI, SAT, SUN = (date(2026, 3, 9) + timedelta(days=i) for i in range(7))


# ---------------------------------------------------------------- durations

def test_net_minutes_deducts_break():
    assert session_net_minutes(at(MON, 9), at(MON, 18), 60) == 480


def test_net_minutes_truncates_partial_minutes():
    assert session_net_minutes(at(MON, 9), at(MON, 9) + timedelta(minutes=10, seconds=59)) == 10


@pytest.mark.parametrize("out_h, brk", [(9, 0), (8, 0)])
def test_checkout_not_after_checkin_rejected(out_h, brk):
    with pytest.raises(InvalidSessionTimes):
        session_net_minutes(at(MON, 9), at(MON, out_h), brk)


def test_break_longer_than_session_rejected():
    with pytest.raises(InvalidSessionTimes):
        session_net_minutes(at(MON, 9), at(MON, 10), 61)


def test_naive_timestamps_rejected():
    with pytest.raises(InvalidSessionTimes):
        session_net_minutes(datetime(2026, 3, 9, 9), datetime(2026, 3, 9, 10))


# ----------------------------------------------------------------- statuses

def test_multiple_sessions_one_day_count_once_and_sum_minutes():
    recs = records(MON, MON, [done(MON, 9, 30, 13, 0), done(MON, 13, 45, 18, 15)])
    assert recs[0].status == DayStatus.PRESENT
    assert recs[0].net_minutes == 210 + 270
    assert recs[0].completed_sessions == 2
    s = summarize(recs, MON, MON)
    assert s.present_days == 1 and s.expected_days == 1 and s.attendance_percentage == 100.0


def test_absent_weekend_and_percentage():
    recs = records(MON, SUN, [done(MON, 9, 30, 18, 30, 60), done(TUE, 9, 30, 18, 30, 60)], today=date(2026, 3, 16))
    statuses = [r.status for r in recs]
    assert statuses == [DayStatus.PRESENT, DayStatus.PRESENT, DayStatus.ABSENT, DayStatus.ABSENT, DayStatus.ABSENT,
                        DayStatus.WEEKEND, DayStatus.WEEKEND]
    s = summarize(recs, MON, SUN)
    assert s.expected_days == 5 and s.present_expected_days == 2 and s.absent_days == 3
    assert s.attendance_percentage == 40.0
    assert s.net_minutes == 960 and s.average_net_minutes == 480
    assert s.required_minutes == 5 * 480 and s.variance_minutes == 960 - 2400


def test_approved_leave_is_not_absence_and_excluded_from_denominator():
    leave = LeaveData(1, TUE, WED, "ANNUAL")
    recs = records(MON, WED, [done(MON, 9, 30, 18, 30, 60)], leaves=[leave])
    assert [r.status for r in recs] == [DayStatus.PRESENT, DayStatus.ON_LEAVE, DayStatus.ON_LEAVE]
    s = summarize(recs, MON, WED)
    assert s.leave_days == 2 and s.absent_days == 0 and s.expected_days == 1
    assert s.attendance_percentage == 100.0 and s.required_minutes == 480


def test_attending_on_leave_day_counts_as_present():
    recs = records(TUE, TUE, [done(TUE, 9, 30, 18, 30)], leaves=[LeaveData(1, TUE, TUE, "CASUAL")])
    assert recs[0].status == DayStatus.PRESENT and recs[0].expected


def test_holiday_excluded_and_department_specific_holiday():
    holidays = [HolidayData(TUE, "Founders' Day", None), HolidayData(WED, "Other dept offsite", 99),
                HolidayData(THU, "Own dept offsite", 10)]
    recs = records(TUE, THU, holidays=holidays)
    assert [r.status for r in recs] == [DayStatus.HOLIDAY, DayStatus.ABSENT, DayStatus.HOLIDAY]
    assert recs[0].holiday_name == "Founders' Day"
    assert summarize(recs, TUE, THU).expected_days == 1


def test_work_on_holiday_is_extra_day_not_in_denominator():
    recs = records(TUE, TUE, [done(TUE, 10, 0, 14, 0)], holidays=[HolidayData(TUE, "Holiday", None)])
    s = summarize(recs, TUE, TUE)
    assert recs[0].status == DayStatus.PRESENT and not recs[0].expected
    assert s.extra_present_days == 1 and s.expected_days == 0 and s.attendance_percentage is None


def test_zero_scheduled_days_gives_no_percentage_without_dividing_by_zero():
    s = summarize(records(SAT, SUN), SAT, SUN)
    assert s.expected_days == 0 and s.attendance_percentage is None and s.average_net_minutes is None


def test_missing_checkout_is_incomplete_and_excluded_from_totals():
    recs = records(MON, MON, [open_s(MON, 9, 30, SessionStatus.MISSING_CHECKOUT)])
    assert recs[0].status == DayStatus.INCOMPLETE
    assert recs[0].net_minutes == 0 and EXC_MISSING_CHECKOUT in recs[0].exceptions
    s = summarize(recs, MON, MON)
    assert s.incomplete_days == 1 and s.present_days == 0 and s.expected_days == 1
    assert s.attendance_percentage == 0.0 and s.unresolved_sessions == 1


def test_stale_open_session_is_unresolved_even_if_status_open():
    recs = records(MON, MON, [open_s(MON, 9)], today=WED, now=at(WED, 9))
    assert recs[0].status == DayStatus.INCOMPLETE


def test_policy_can_count_incomplete_as_present():
    policy = PolicyData(count_incomplete_as_present=True)
    recs = records(MON, MON, [open_s(MON, 9, 30, SessionStatus.MISSING_CHECKOUT)], policy=policy)
    assert recs[0].status == DayStatus.PRESENT and recs[0].net_minutes == 0
    assert EXC_MISSING_CHECKOUT in recs[0].exceptions


def test_incomplete_session_plus_completed_session_is_present_with_exception():
    recs = records(MON, MON, [done(MON, 9, 30, 13, 0), open_s(MON, 14, 0, SessionStatus.MISSING_CHECKOUT)])
    assert recs[0].status == DayStatus.PRESENT and recs[0].net_minutes == 210
    assert EXC_MISSING_CHECKOUT in recs[0].exceptions


def test_minimum_minutes_policy():
    policy = PolicyData(min_present_minutes=240)
    recs = records(MON, MON, [done(MON, 9, 30, 11, 0)], policy=policy)
    assert recs[0].status == DayStatus.ABSENT
    assert recs[0].net_minutes == 90  # presence time is still reported


def test_today_without_checkin_is_not_yet_checked_in_and_not_expected():
    recs = records(FRI, FRI, today=FRI, now=at(FRI, 11))
    assert recs[0].status == DayStatus.NOT_YET_CHECKED_IN and not recs[0].expected


def test_today_with_active_session_is_present_but_adds_no_minutes():
    recs = records(FRI, FRI, [open_s(FRI, 9, 30)], today=FRI, now=at(FRI, 11))
    r = recs[0]
    assert r.status == DayStatus.PRESENT and r.has_active_session and r.net_minutes == 0


def test_future_dates_are_not_evaluated():
    assert len(records(THU, date(2026, 3, 20), today=FRI)) == 2


def test_overnight_session_belongs_to_start_day():
    night = ScheduleData(2, 1, None, time(21, 0), time(6, 0), 480, frozenset({1, 2, 3, 4, 5}), date(2024, 1, 1), None)
    s = done(MON, 21, 0, 6, 0, 60, day2=TUE)
    recs = records(MON, TUE, [s], schedules=[WEEKDAYS, night])
    assert recs[0].status == DayStatus.PRESENT and recs[0].net_minutes == 480
    assert recs[1].status == DayStatus.ABSENT and recs[1].net_minutes == 0


def test_running_overnight_session_is_active_not_missing():
    recs = records(MON, TUE, [open_s(MON, 21, 0)], today=TUE, now=at(TUE, 2))
    assert recs[0].status == DayStatus.PRESENT and recs[0].has_active_session
    assert EXC_MISSING_CHECKOUT not in recs[0].exceptions


def test_dst_zone_day_boundaries():
    ny = ZoneInfo("America/New_York")
    d = date(2026, 3, 8)  # DST starts 02:00 local
    a = datetime(2026, 3, 8, 1, 30, tzinfo=ny).astimezone(UTC)
    b = datetime(2026, 3, 8, 4, 30, tzinfo=ny).astimezone(UTC)
    assert session_net_minutes(a, b) == 120  # wall clock shows 3h but only 2h elapsed
    sched = ScheduleData(1, None, None, time(9), time(17), 480, frozenset(range(1, 8)), date(2024, 1, 1), None)
    sess = SessionData(1, 1, a, b, 0, 120, SessionStatus.COMPLETED)
    recs = build_day_records(EMP, d, d, today=date(2026, 3, 9), now=b + timedelta(days=1), tz=ny,
                             sessions=[sess], schedules=[sched], holidays=[], leaves=[], policy=POLICY)
    assert recs[0].date == d and recs[0].net_minutes == 120


def test_utc_vs_local_date_boundary():
    # 23:00 UTC on Monday is 04:30 Tuesday in IST: the session is Tuesday's.
    a = datetime(2026, 3, 9, 23, 0, tzinfo=UTC)
    s = SessionData(1, 1, a, a + timedelta(hours=4), 0, 240, SessionStatus.COMPLETED)
    recs = records(MON, TUE, [s])
    assert recs[0].net_minutes == 0 and recs[1].net_minutes == 240


def test_late_arrival_and_short_hours_exceptions():
    recs = records(MON, MON, [done(MON, 10, 0, 16, 0)])
    assert EXC_LATE_ARRIVAL in recs[0].exceptions and EXC_SHORT_HOURS in recs[0].exceptions
    on_time = records(TUE, TUE, [done(TUE, 9, 40, 18, 40, 30)])
    assert on_time[0].exceptions == []


def test_days_before_joining_are_not_scheduled():
    emp = EmployeeData(1, 10, date_joined=WED)
    recs = records(MON, WED, employee=emp)
    assert [r.status for r in recs] == [DayStatus.NOT_SCHEDULED, DayStatus.NOT_SCHEDULED, DayStatus.ABSENT]


def test_voided_sessions_are_ignored():
    voided = SessionData(99, 1, at(MON, 9), at(MON, 18), 0, 540, SessionStatus.VOIDED)
    assert records(MON, MON, [voided])[0].status == DayStatus.ABSENT


def test_schedule_resolution_priority_and_effective_dates():
    dept = ScheduleData(2, None, 10, time(8), time(16), 450, frozenset({1, 2, 3, 4, 5, 6}), date(2024, 1, 1), None)
    personal = ScheduleData(3, 1, None, time(12), time(20), 420, frozenset({1, 2, 3}), date(2026, 3, 11), None)
    old = ScheduleData(4, None, None, time(9), time(17), 400, frozenset({1}), date(2023, 1, 1), date(2023, 12, 31))
    schedules = [WEEKDAYS, dept, personal, old]
    assert resolve_schedule(schedules, EMP, TUE).id == 2   # department beats org default
    assert resolve_schedule(schedules, EMP, WED).id == 3   # personal from its effective date
    assert resolve_schedule([old], EMP, TUE) is None       # expired assignment


def test_schedule_based_required_minutes_and_working_days():
    dept = ScheduleData(2, None, 10, time(8), time(16), 450, frozenset({1, 2, 3, 4, 5, 6}), date(2024, 1, 1), None)
    recs = records(SAT, SAT, [done(SAT, 8, 0, 16, 0, 30)], schedules=[WEEKDAYS, dept], today=date(2026, 3, 16))
    assert recs[0].status == DayStatus.PRESENT and recs[0].expected and recs[0].required_minutes == 450
    s = summarize(recs, SAT, SAT)
    assert s.variance_minutes == 0
