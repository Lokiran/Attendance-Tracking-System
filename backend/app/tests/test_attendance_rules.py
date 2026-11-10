"""Unit tests for the pure attendance rules engine (no database). Attendance comes from imported timesheets."""

from datetime import UTC, date, datetime, time, timedelta
from zoneinfo import ZoneInfo

from app.models import DayStatus, WorkMode
from app.services.attendance_rules import (
    EXC_ABSENT,
    EXC_BELOW_MINIMUM,
    EXC_LATE_ARRIVAL,
    EXC_SHORT_HOURS,
    EmployeeData,
    HolidayData,
    PolicyData,
    ScheduleData,
    TimesheetDay,
    build_day_records,
    resolve_schedule,
    summarize,
)

IST = ZoneInfo("Asia/Kolkata")
EMP = EmployeeData(id=1, department_id=10, date_joined=date(2025, 1, 1))
WEEKDAYS = ScheduleData(1, None, None, time(9, 30), time(18, 30), 480, frozenset({1, 2, 3, 4, 5}),
                        date(2024, 1, 1), None)
POLICY = PolicyData()
MON, TUE, WED, THU, FRI, SAT, SUN = (date(2026, 3, 9) + timedelta(days=i) for i in range(7))


def at(d: date, h: int, m: int = 0) -> datetime:
    return datetime.combine(d, time(h, m), tzinfo=IST).astimezone(UTC)


def office(d: date, minutes: int | None = None, first_in: datetime | None = None, last_out=None) -> TimesheetDay:
    return TimesheetDay(1, d, WorkMode.OFFICE, minutes, first_in, last_out)


def mode(d: date, m: WorkMode) -> TimesheetDay:
    return TimesheetDay(1, d, m)


def records(start, end, timesheet=(), *, today=date(2026, 3, 13), schedules=(WEEKDAYS,), holidays=(),
            policy=POLICY, employee=EMP, covered=None):
    kw = {} if covered is None else {"covered": covered}
    return build_day_records(employee, start, end, today=today, tz=IST, timesheet=list(timesheet),
                             schedules=list(schedules), holidays=list(holidays), policy=policy, **kw)


# ---------------------------------------------------------------- statuses

def test_office_days_are_present_and_missing_covered_days_absent():
    recs = records(MON, SUN, [office(MON, 480), office(TUE), mode(WED, WorkMode.WFH)], today=date(2026, 3, 16))
    assert [r.status for r in recs] == [DayStatus.PRESENT, DayStatus.PRESENT, DayStatus.WFH, DayStatus.ABSENT,
                                        DayStatus.ABSENT, DayStatus.WEEKEND, DayStatus.WEEKEND]
    assert recs[3].exceptions == [EXC_ABSENT]
    s = summarize(recs, MON, SUN)
    assert (s.expected_days, s.present_expected_days, s.wfh_days, s.absent_days) == (5, 2, 1, 2)
    assert s.attendance_percentage == 40.0  # office attendance: WFH counts as a working day, not present


def test_days_without_a_covering_timesheet_are_not_absent():
    covered = lambda d: d <= TUE  # noqa: E731 - the September sheet reached Tuesday only
    recs = records(MON, FRI, [office(MON)], covered=covered)
    assert [r.status for r in recs] == [DayStatus.PRESENT, DayStatus.ABSENT, DayStatus.NO_TIMESHEET,
                                        DayStatus.NO_TIMESHEET, DayStatus.NO_TIMESHEET]
    s = summarize(recs, MON, FRI)
    assert s.expected_days == 2 and s.no_timesheet_days == 3 and s.attendance_percentage == 50.0


def test_today_without_data_is_never_absent():
    recs = records(FRI, FRI)
    assert recs[0].status == DayStatus.NO_TIMESHEET and not recs[0].expected


def test_leave_holiday_and_weekoff_rows_are_not_expected():
    recs = records(MON, WED, [mode(MON, WorkMode.LEAVE), mode(TUE, WorkMode.HOLIDAY), mode(WED, WorkMode.WEEKOFF)])
    assert [r.status for r in recs] == [DayStatus.LEAVE, DayStatus.HOLIDAY, DayStatus.WEEKEND]
    assert not any(r.expected for r in recs)
    s = summarize(recs, MON, WED)
    assert s.leave_days == 1 and s.attendance_percentage is None


# ---------------------------------------------------------------- hours

def test_days_without_hours_are_present_without_short_hours():
    recs = records(MON, TUE, [office(MON), office(TUE)])
    assert all(r.status == DayStatus.PRESENT and not r.hours_known and r.exceptions == [] for r in recs)
    s = summarize(recs, MON, TUE)
    assert s.net_minutes == 0 and s.hours_days == 0 and s.average_net_minutes is None
    assert s.variance_minutes == 0  # never "hours short" when the timesheet has no hours


def test_recorded_hours_are_measured_against_the_shift_of_the_same_days():
    recs = records(MON, WED, [office(MON, 480), office(TUE, 420), office(WED)])
    s = summarize(recs, MON, WED)
    assert s.net_minutes == 900 and s.hours_days == 2 and s.hours_required_minutes == 960
    assert s.variance_minutes == -60 and s.average_net_minutes == 450
    assert s.required_minutes == 1440  # all expected days, for the "shift hours" figure
    assert EXC_SHORT_HOURS in recs[1].exceptions and EXC_SHORT_HOURS not in recs[2].exceptions


def test_late_arrival_from_timesheet_in_time():
    recs = records(MON, TUE, [office(MON, 480, first_in=at(MON, 10, 0)), office(TUE, 480, first_in=at(TUE, 9, 40))])
    assert recs[0].exceptions == [EXC_LATE_ARRIVAL] and recs[1].exceptions == []
    assert summarize(recs, MON, TUE).late_days == 1


def test_minimum_minutes_policy_applies_only_to_recorded_hours():
    policy = PolicyData(min_present_minutes=240)
    recs = records(MON, TUE, [office(MON, 120), office(TUE)], policy=policy)
    assert recs[0].status == DayStatus.ABSENT and recs[0].exceptions == [EXC_BELOW_MINIMUM]
    assert recs[1].status == DayStatus.PRESENT


# ---------------------------------------------------------------- calendar

def test_holiday_excluded_and_department_specific_holiday():
    hols = [HolidayData(TUE, "Founders Day", None), HolidayData(WED, "Team day", 99)]
    recs = records(MON, WED, [office(MON)], holidays=hols)
    assert [r.status for r in recs] == [DayStatus.PRESENT, DayStatus.HOLIDAY, DayStatus.ABSENT]
    assert recs[1].holiday_name == "Founders Day"


def test_work_on_an_off_day_is_an_extra_day_not_in_the_denominator():
    recs = records(SAT, SAT, [office(SAT, 300)], today=date(2026, 3, 16))
    assert recs[0].status == DayStatus.PRESENT and not recs[0].expected
    s = summarize(recs, SAT, SAT)
    assert s.extra_present_days == 1 and s.expected_days == 0 and s.attendance_percentage is None


def test_future_dates_are_not_evaluated():
    assert records(FRI, date(2026, 3, 20)) == records(FRI, FRI)


def test_days_before_joining_are_not_scheduled_even_with_data():
    emp = EmployeeData(1, 10, date_joined=WED)
    recs = records(MON, WED, [office(MON)], employee=emp)
    assert [r.status for r in recs] == [DayStatus.NOT_SCHEDULED, DayStatus.NOT_SCHEDULED, DayStatus.ABSENT]


def test_other_employees_rows_are_ignored():
    other = TimesheetDay(2, MON, WorkMode.OFFICE)
    assert records(MON, MON, [other])[0].status == DayStatus.ABSENT


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
    recs = records(SAT, SAT, [office(SAT, 450)], schedules=[WEEKDAYS, dept], today=date(2026, 3, 16))
    assert recs[0].status == DayStatus.PRESENT and recs[0].expected and recs[0].required_minutes == 450
    assert summarize(recs, SAT, SAT).variance_minutes == 0


def test_shared_memo_gives_the_same_result():
    shared: dict = {}
    a = build_day_records(EMP, MON, FRI, today=date(2026, 3, 16), tz=IST, timesheet=[office(MON)],
                          schedules=[WEEKDAYS], holidays=[], policy=POLICY, shared=shared)
    b = build_day_records(EMP, MON, FRI, today=date(2026, 3, 16), tz=IST, timesheet=[office(MON)],
                          schedules=[WEEKDAYS], holidays=[], policy=POLICY, shared=shared)
    assert a == b and shared


def test_timesheet_fast_path_matches_the_full_classifier():
    """attendance_data.timesheet_day short-cuts plain rows; it must agree with wfo_rules.classify_day."""
    from app.services import attendance_data
    from app.services.wfo_rules import DayEvidence, classify_day

    t_in, t_out = at(MON, 9, 30), at(MON, 18, 0)
    for mode in (None, *WorkMode):
        for minutes in (None, 0, 300):
            fast = attendance_data.timesheet_day(1, MON, None, mode, minutes, None, None, t_in, t_out)
            cls = classify_day(DayEvidence(upload_mode=mode, upload_minutes=minutes))
            if cls.work_mode is None:
                assert fast is None, (mode, minutes)
                continue
            expected = cls.office_minutes if cls.work_mode == WorkMode.OFFICE and cls.hours_known else None
            assert (fast.work_mode, fast.minutes) == (cls.work_mode, expected), (mode, minutes)
    # rows with an override, badge or Wi-Fi go through the full classifier
    badge = attendance_data.timesheet_day(1, MON, None, WorkMode.WFH, None, 240, None, t_in, t_out)
    assert badge.work_mode == WorkMode.OFFICE and badge.minutes == 240
