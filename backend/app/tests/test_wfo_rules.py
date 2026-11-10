"""WFO Phase 1 (classification priority, conflicts, text mapping) and Phase 2 (policy compliance)."""

from datetime import date, timedelta

import pytest

from app.core.wfo_mapping import get_mapping, parse_mapping
from app.models.enums import DailyStatus, WfoSource, WorkMode
from app.services.wfo_rules import (
    DayEvidence,
    PolicyDay,
    WfoPolicy,
    classify_day,
    day_status,
    round_half_up,
    summarize_wfo,
)

O, H = WorkMode.OFFICE, WorkMode.WFH


# ------------------------------------------------------------------ text mapping (config file)

@pytest.mark.parametrize("text, mode", [
    ("WFO", O), ("Office", O), ("On-site", O), ("ON_SITE", O), ("  on   site ", O), ("onsite", O),
    ("WFH", H), ("Remote", H), ("work from home", H), ("Leave", WorkMode.LEAVE), ("Week Off", WorkMode.WEEKOFF),
    ("holiday", WorkMode.HOLIDAY), ("OFFICE", O),
])
def test_work_mode_text_mapping_from_config(text, mode):
    assert get_mapping().work_mode(text) == mode


def test_unknown_text_and_badge_sources():
    m = get_mapping()
    assert m.work_mode("hybrid-ish") is None and m.work_mode("") is None
    assert m.is_badge("Badge") and m.is_badge("card-swipe") and not m.is_badge("HRMS")
    assert m.column("Employee Code") == "employee" and m.column("Work Location") == "work_mode"
    assert m.column("Status") is None  # deliberately not a work-mode column (often Present / Absent)


def test_mapping_file_is_validated():
    with pytest.raises(ValueError, match="unknown work mode"):
        parse_mapping({"work_modes": {"HYBRID": ["hybrid"]}})
    with pytest.raises(ValueError, match="both"):
        parse_mapping({"work_modes": {"OFFICE": ["x"], "WFH": ["x"]}})
    with pytest.raises(ValueError, match="unknown column"):
        parse_mapping({"columns": {"salary": ["pay"]}})


# ------------------------------------------------------------------ classification priority

def test_priority_1_upload_column_beats_lower_sources():
    c = classify_day(DayEvidence(upload_mode=O, upload_minutes=480, wifi_minutes=300, checkin_office_seen=True))
    assert (c.work_mode, c.source, c.office_minutes) == (O, WfoSource.UPLOAD_COLUMN, 480)


def test_priority_2_badge_then_3_wifi_then_4_checkin():
    assert classify_day(DayEvidence(badge_minutes=400, wifi_minutes=500)).source == WfoSource.BADGE
    assert classify_day(DayEvidence(wifi_minutes=500, checkin_office_minutes=450)).source == WfoSource.WIFI
    assert classify_day(DayEvidence(checkin_office_minutes=450)).source == WfoSource.CHECK_IN
    # minutes: the longest office evidence, never the sum
    assert classify_day(DayEvidence(badge_minutes=400, wifi_minutes=500)).office_minutes == 500


def test_priority_5_hours_without_office_signal_are_wfh():
    c = classify_day(DayEvidence(checkin_untagged_minutes=420))
    assert (c.work_mode, c.source, c.wfh_minutes) == (H, WfoSource.INFERRED, 420)
    assert classify_day(DayEvidence(upload_minutes=360)).work_mode == H  # hours in file, no mode, no badge


def test_upload_wfh_and_checkin_wfh():
    assert classify_day(DayEvidence(upload_mode=H, upload_minutes=480)).source == WfoSource.UPLOAD_COLUMN
    c = classify_day(DayEvidence(checkin_wfh_minutes=470, checkin_wfh_seen=True))
    assert (c.work_mode, c.source, c.wfh_minutes) == (H, WfoSource.CHECK_IN, 470)


def test_conflict_office_and_wfh_marks_office_and_logs_it():
    c = classify_day(DayEvidence(upload_mode=H, upload_minutes=480, badge_minutes=420))
    assert c.work_mode == O and c.source == WfoSource.BADGE and c.office_minutes == 420
    assert c.conflicts == ("UPLOAD_COLUMN said WFH; office signal from BADGE",)
    c = classify_day(DayEvidence(wifi_minutes=300, checkin_wfh_seen=True, checkin_wfh_minutes=60))
    assert c.work_mode == O and c.conflicts == ("CHECK_IN said WFH; office signal from WIFI",)


def test_office_signal_on_leave_day_wins_with_conflict():
    c = classify_day(DayEvidence(upload_mode=WorkMode.LEAVE, badge_minutes=300))
    assert c.work_mode == O and "UPLOAD_COLUMN said LEAVE" in c.conflicts[0]


def test_leave_holiday_weekoff_from_file_and_calendar():
    assert classify_day(DayEvidence(upload_mode=WorkMode.LEAVE)).work_mode == WorkMode.LEAVE
    assert classify_day(DayEvidence(holiday=True)).work_mode == WorkMode.HOLIDAY
    c = classify_day(DayEvidence(scheduled_workday=False))
    assert (c.work_mode, c.source) == (WorkMode.WEEKOFF, WfoSource.CALENDAR)
    assert classify_day(DayEvidence()).work_mode is None  # scheduled day, nothing at all: absent
    c = classify_day(DayEvidence(upload_mode=WorkMode.LEAVE, checkin_untagged_minutes=120))
    assert c.work_mode == WorkMode.LEAVE and c.conflicts  # leave in file but hours checked in


def test_working_on_a_holiday_or_weekend_is_still_classified():
    assert classify_day(DayEvidence(holiday=True, checkin_office_minutes=300)).work_mode == O
    assert classify_day(DayEvidence(scheduled_workday=False, checkin_untagged_minutes=200)).work_mode == H


def test_override_always_wins():
    c = classify_day(DayEvidence(override=H, badge_minutes=480, upload_mode=O))
    assert (c.work_mode, c.source, c.conflicts) == (H, WfoSource.MANUAL_OVERRIDE, ())
    c = classify_day(DayEvidence(override=O, wifi_minutes=300))
    assert c.office_minutes == 300
    # overriding to OFFICE claims hours checked in without a location
    c = classify_day(DayEvidence(override=O, checkin_untagged_minutes=480))
    assert (c.office_minutes, c.wfh_minutes) == (480, 0)
    c = classify_day(DayEvidence(override=WorkMode.LEAVE, checkin_untagged_minutes=480))
    assert (c.office_minutes, c.wfh_minutes) == (0, 0)


def test_day_status_from_minutes():
    full, half = 480, 240
    assert day_status(classify_day(DayEvidence(badge_minutes=500)), full, half) == DailyStatus.PRESENT
    assert day_status(classify_day(DayEvidence(badge_minutes=300)), full, half) == DailyStatus.HALF_DAY
    assert day_status(classify_day(DayEvidence(badge_minutes=0)), full, half) == DailyStatus.ABSENT
    assert day_status(classify_day(DayEvidence(upload_mode=WorkMode.LEAVE)), full, half) == DailyStatus.LEAVE
    assert day_status(classify_day(DayEvidence(scheduled_workday=False)), full, half) == DailyStatus.WEEKEND


# ------------------------------------------------------------------ policy

POLICY = WfoPolicy(min_days_per_week=3, min_days_per_month=12, min_office_minutes=240)
NOV_START, NOV_END = date(2026, 11, 1), date(2026, 11, 30)  # 1 Nov 2026 is a Sunday


def weekday(d: date) -> bool:
    return d.weekday() < 5


def month(modes: dict[date, tuple[WorkMode | None, int]], *, holidays=(), start=NOV_START, end=NOV_END):
    """Every day of the period; unspecified weekdays are absent, weekends WEEKOFF."""
    out = []
    d = start
    while d <= end:
        sched = weekday(d) and d not in holidays
        mode, minutes = modes.get(d, (WorkMode.HOLIDAY if d in holidays else (None if weekday(d) else WorkMode.WEEKOFF), 0))
        out.append(PolicyDay(d, mode, minutes, sched))
        d += timedelta(days=1)
    return out


def summary(days, today=date(2026, 12, 15), start=NOV_START, end=NOV_END, policy=POLICY):
    return summarize_wfo(days, start=start, end=end, today=today, policy=policy, is_scheduled=weekday)


def office(*days, minutes=480):
    return {d: (O, minutes) for d in days}


def wfh(*days):
    return {d: (H, 480) for d in days}


def weekdays_of(start: date, end: date):
    d, out = start, []
    while d <= end:
        if weekday(d):
            out.append(d)
        d += timedelta(days=1)
    return out


def test_minimum_hours_rule():
    days = month({**office(date(2026, 11, 2)), **office(date(2026, 11, 3), minutes=239),
                  **office(date(2026, 11, 4), minutes=240)})
    s = summary(days)
    assert s.wfo_days == 2 and s.office_days_below_minimum == 1  # 239 min is OFFICE but not a WFO day


def test_fully_compliant_month_three_days_a_week():
    wd = weekdays_of(NOV_START, NOV_END)  # 21 weekdays in Nov 2026
    office_days = [d for d in wd if d.weekday() in (0, 1, 2)]
    s = summary(month({**office(*office_days), **wfh(*[d for d in wd if d not in office_days])}))
    assert s.working_days == 21 and s.standard_days == 21
    assert s.wfo_days == len(office_days) == 13 and s.wfh_days == 8  # 5 Mondays + 4 Tuesdays + 4 Wednesdays
    assert s.required_days == 12 and s.shortfall_days == 0 and s.compliance == "COMPLIANT"
    assert s.wfo_percentage == round(13 * 100 / 21, 1)
    assert all(w.status == "MET" for w in s.weeks)


def test_short_month_is_non_compliant_with_shortfall():
    wd = weekdays_of(NOV_START, NOV_END)
    s = summary(month({**office(*wd[:8]), **wfh(*wd[8:])}))
    assert s.wfo_days == 8 and s.required_days == 12
    assert s.compliance == "NON_COMPLIANT" and s.shortfall_days >= 4


def test_leave_week_is_excluded_and_lowers_the_requirement():
    wd = weekdays_of(NOV_START, NOV_END)
    leave = [d for d in wd if date(2026, 11, 9) <= d <= date(2026, 11, 13)]  # a whole week of leave
    rest = [d for d in wd if d not in leave]
    office_days = [d for d in rest if d.weekday() in (0, 1, 2)]
    days = month({**{d: (WorkMode.LEAVE, 0) for d in leave}, **office(*office_days),
                  **wfh(*[d for d in rest if d not in office_days])})
    s = summary(days)
    assert s.working_days == 16 and s.leave_days == 5
    assert s.required_days == round_half_up(12 * 16 / 21) == 9
    leave_week = next(w for w in s.weeks if w.week_start == date(2026, 11, 9))
    assert leave_week.working_days == 0 and leave_week.required == 0 and leave_week.status == "MET"
    assert s.compliance == "COMPLIANT"


def test_holiday_week_and_partial_weeks_are_prorated():
    holiday = date(2026, 11, 4)  # Wednesday
    wd = [d for d in weekdays_of(NOV_START, NOV_END) if d != holiday]
    days = month(office(*wd), holidays={holiday})
    s = summary(days)
    assert s.standard_days == 20 and s.holiday_days == 1
    week = next(w for w in s.weeks if w.week_start == date(2026, 11, 2))
    assert week.working_days == 4 and week.required == round_half_up(3 * 4 / 5) == 2
    # the week of 30 Nov has one day inside November: requirement round(3 * 1 / 5) = 1
    last = s.weeks[-1]
    assert last.week_start == date(2026, 11, 30) and last.working_days == 1 and last.required == 1
    # the first "week" (Sun 1 Nov only) has no working days
    assert s.weeks[0].working_days == 0 and s.weeks[0].required == 0


def test_one_short_week_makes_the_month_non_compliant_even_if_total_is_met():
    wd = weekdays_of(NOV_START, NOV_END)
    week2 = [d for d in wd if date(2026, 11, 9) <= d <= date(2026, 11, 13)]
    office_days = [d for d in wd if d not in week2]  # 16 office days, but week 2 has none
    s = summary(month({**office(*office_days), **wfh(*week2)}))
    assert s.wfo_days == 16 >= s.required_days
    assert s.compliance == "NON_COMPLIANT" and s.shortfall_days == 3


def test_office_day_on_a_weekend_counts_and_widens_the_denominator():
    wd = weekdays_of(NOV_START, NOV_END)
    s = summary(month({**office(*wd), **office(date(2026, 11, 7))}))  # Saturday in the office
    assert s.working_days == 22 and s.wfo_days == 22 and s.wfo_percentage == 100.0


def test_current_month_counts_completed_days_only():
    today = date(2026, 11, 11)  # Wednesday
    wd = weekdays_of(NOV_START, NOV_END)
    days = month({**office(date(2026, 11, 2), date(2026, 11, 3), date(2026, 11, 4), date(2026, 11, 9),
                         date(2026, 11, 11)), **wfh(*[d for d in wd if d < today and d.weekday() > 2])})
    s = summary(days, today=today)
    assert s.working_days == 7  # 2-6 and 9-10 November; today (the 11th) is not counted yet
    assert s.wfo_days == 4
    current = next(w for w in s.weeks if w.week_start == date(2026, 11, 9))
    assert current.status == "IN_PROGRESS" and current.shortfall == 0
    assert [w.status for w in s.weeks if w.week_start > today] == ["UPCOMING"] * 3
    assert s.required_days == round_half_up(12 * 7 / 21) == 4 and s.compliance == "COMPLIANT"


def test_no_working_days_is_compliant_with_no_percentage():
    days = month({d: (WorkMode.LEAVE, 0) for d in weekdays_of(NOV_START, NOV_END)})
    s = summary(days)
    assert s.working_days == 0 and s.wfo_percentage is None and s.compliance == "COMPLIANT"


def test_working_on_a_holiday_never_raises_the_requirement():
    holiday = date(2026, 11, 4)
    wd = [d for d in weekdays_of(NOV_START, NOV_END) if d != holiday]
    days = month({**office(*wd), **wfh(holiday)}, holidays={holiday})
    for i, d in enumerate(days):  # the holiday was worked from home
        if d.date == holiday:
            days[i] = PolicyDay(holiday, H, 0, False)
    s = summary(days)
    assert s.standard_days == 20 and s.working_days == 21  # the worked holiday counts for the percentage...
    assert s.required_days == 12                           # ...but the requirement stays at the policy's 12
    week = next(w for w in s.weeks if w.week_start == date(2026, 11, 2))
    assert week.working_days == 5 and week.required == round_half_up(3 * 4 / 5) == 2


def test_a_week_straddling_two_months_is_judged_on_all_its_days():
    """1-2 Oct 2026 are Thursday and Friday; their week's office days (Mon-Wed 28-30 Sep) are in September."""
    start, end = date(2026, 10, 1), date(2026, 10, 31)
    days = []
    d = date(2026, 9, 25)
    while d <= date(2026, 11, 6):
        wd = d.weekday() < 5
        mode = (O if d.weekday() <= 2 else H) if wd else WorkMode.WEEKOFF
        days.append(PolicyDay(d, mode, 480 if mode in (O, H) else 0, wd))
        d += timedelta(days=1)
    s = summary(days, today=date(2026, 12, 1), start=start, end=end)
    first = s.weeks[0]
    assert first.week_start == date(2026, 9, 28) and first.wfo_days == 3 and first.required == 3
    assert first.status == "MET"  # judged on Mon 28 Sep - Sun 4 Oct, not just Thu-Fri
    assert s.wfo_days == 12 and s.compliance == "COMPLIANT"  # monthly figures still count October only
