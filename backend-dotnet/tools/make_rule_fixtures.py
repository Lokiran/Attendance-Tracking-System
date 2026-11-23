"""Generate rule-engine parity fixtures: randomised inputs run through the Python backend's own pure rules
(attendance_rules, wfo_rules, round / round_half_up), saved with their outputs as JSON. The .NET tests replay every case
and must produce identical results. Synthetic data only; the seed is fixed so the files are reproducible.

    backend\\.venv\\Scripts\\python.exe backend-dotnet\\tools\\make_rule_fixtures.py
"""
import gzip
import json
import random
import sys
from datetime import UTC, date, datetime, time, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "backend"))
from app.models.enums import WorkMode  # noqa: E402
from app.services.attendance_rules import (  # noqa: E402
    EmployeeData, HolidayData, PolicyData, ScheduleData, TimesheetDay, attendance_rate, build_day_records, summarize,
)
from app.services.wfo_rules import (  # noqa: E402
    DayEvidence, PolicyDay, WfoPolicy, classify_day, day_status, round_half_up, summarize_wfo,
)

OUT = ROOT / "backend-dotnet" / "tests" / "ATS.Tests" / "Fixtures"
rnd = random.Random(20261120)
ZONES = ["Asia/Kolkata", "Europe/London", "America/New_York"]
MODES = [WorkMode.OFFICE, WorkMode.OFFICE, WorkMode.OFFICE, WorkMode.WFH, WorkMode.LEAVE, WorkMode.HOLIDAY, WorkMode.WEEKOFF]


def d2s(d): return d.isoformat() if d else None
def dt2s(t): return t.astimezone(UTC).isoformat() if t else None
def rdate(lo: date, hi: date) -> date: return lo + timedelta(days=rnd.randint(0, (hi - lo).days))


def attendance_case(i: int) -> dict:
    zone = rnd.choice(ZONES)
    tz = ZoneInfo(zone)
    start = rdate(date(2026, 1, 1), date(2026, 10, 1))
    end = start + timedelta(days=rnd.randint(0, 75))
    today = rdate(start - timedelta(days=5), end + timedelta(days=20))
    dept = rnd.choice([None, 10, 20])
    joined = rdate(start - timedelta(days=60), end)
    emp = EmployeeData(1, dept, joined)
    schedules = []
    for sid in range(1, rnd.randint(1, 4) + 1):
        tier = rnd.choice(["org", "org", "dept", "emp"])
        days = sorted(rnd.sample(range(1, 8), rnd.randint(1, 6)))
        eff_from = rdate(start - timedelta(days=90), end)
        eff_to = rdate(eff_from, eff_from + timedelta(days=120)) if rnd.random() < 0.3 else None
        schedules.append(ScheduleData(
            sid, 1 if tier == "emp" else (2 if rnd.random() < 0.2 and tier == "emp" else None),
            rnd.choice([10, 20]) if tier == "dept" else None,
            time(rnd.choice([1, 2, 9, 10, 22]), rnd.choice([0, 30])), time(rnd.choice([6, 17, 18]), 0),
            rnd.choice([420, 450, 480]), frozenset(days), eff_from, eff_to, f"S{sid}"))
    holidays = [HolidayData(rdate(start, end), f"H{h}", rnd.choice([None, None, dept, 99])) for h in range(rnd.randint(0, 5))]
    timesheet = []
    for d in sorted({rdate(start - timedelta(days=3), end) for _ in range(rnd.randint(0, 30))}):
        mode = rnd.choice(MODES)
        minutes = rnd.choice([None, None, 0, 120, 239, 240, 420, 480, 600]) if mode == WorkMode.OFFICE else None
        first_in = datetime.combine(d, time(rnd.randint(0, 23), rnd.randint(0, 59)), tzinfo=tz) if rnd.random() < 0.7 else None
        last_out = first_in + timedelta(minutes=rnd.randint(0, 700)) if first_in and rnd.random() < 0.8 else None
        timesheet.append(TimesheetDay(1, d, mode, minutes, first_in, last_out))
    timesheet.append(TimesheetDay(2, start, WorkMode.OFFICE, 480))  # another employee's row: ignored
    periods = sorted((p, p + timedelta(days=rnd.randint(0, 40))) for p in [rdate(start - timedelta(days=30), end) for _ in range(rnd.randint(0, 3))])
    use_periods = rnd.random() < 0.85
    covered = (lambda d: any(a <= d <= b for a, b in periods)) if use_periods else (lambda d: True)
    policy = PolicyData(min_present_minutes=rnd.choice([0, 0, 240]), late_grace_minutes=rnd.choice([0, 15, 30]))
    records = build_day_records(emp, start, end, today=today, tz=tz, timesheet=timesheet, schedules=schedules,
                                holidays=holidays, policy=policy, covered=covered)
    s = summarize(records, start, end)
    return {
        "zone": zone, "start": d2s(start), "end": d2s(end), "today": d2s(today),
        "employee": {"id": 1, "department_id": dept, "date_joined": d2s(joined)},
        "schedules": [{"id": x.id, "employee_id": x.employee_id, "department_id": x.department_id,
                       "start_time": x.start_time.isoformat(), "end_time": x.end_time.isoformat(),
                       "required_minutes": x.required_minutes, "working_days": sorted(x.working_days),
                       "effective_from": d2s(x.effective_from), "effective_to": d2s(x.effective_to), "name": x.name} for x in schedules],
        "holidays": [{"date": d2s(h.holiday_date), "name": h.name, "department_id": h.department_id} for h in holidays],
        "timesheet": [{"employee_id": t.employee_id, "date": d2s(t.date), "work_mode": t.work_mode.value if t.work_mode else None,
                       "minutes": t.minutes, "first_in": dt2s(t.first_in), "last_out": dt2s(t.last_out)} for t in timesheet],
        "covered": [[d2s(a), d2s(b)] for a, b in periods] if use_periods else None,  # None: every date covered
        "policy": {"min_present_minutes": policy.min_present_minutes, "late_grace_minutes": policy.late_grace_minutes},
        "records": [{"date": d2s(r.date), "status": r.status.value, "expected": r.expected,
                     "scheduled_workday": r.scheduled_workday, "required_minutes": r.required_minutes,
                     "net_minutes": r.net_minutes, "hours_known": r.hours_known,
                     "work_mode": r.work_mode.value if r.work_mode else None, "first_in": dt2s(r.first_in),
                     "last_out": dt2s(r.last_out), "holiday_name": r.holiday_name, "exceptions": list(r.exceptions)}
                    for r in records],
        "summary": {k: getattr(s, k) for k in (
            "expected_days", "present_days", "present_expected_days", "extra_present_days", "absent_days", "wfh_days",
            "leave_days", "holiday_days", "weekend_days", "not_scheduled_days", "no_timesheet_days", "net_minutes",
            "required_minutes", "hours_days", "hours_required_minutes", "late_days", "variance_minutes",
            "average_net_minutes", "attendance_percentage", "counted_days")},
    }


def evidence_case() -> dict:
    mode = lambda: rnd.choice([None, None, *WorkMode])  # noqa: E731
    e = DayEvidence(
        scheduled_workday=rnd.random() < 0.7, holiday=rnd.random() < 0.15, override=mode() if rnd.random() < 0.2 else None,
        upload_mode=mode(), upload_minutes=rnd.choice([None, None, 0, 90, 240, 480]),
        badge_minutes=rnd.choice([None, None, None, 0, 300]), wifi_minutes=rnd.choice([None, None, 0, 200, 500]),
        checkin_office_minutes=rnd.choice([0, 0, 120, 480]), checkin_wfh_minutes=rnd.choice([0, 0, 60, 300]),
        checkin_untagged_minutes=rnd.choice([0, 0, 45]), checkin_office_seen=rnd.random() < 0.15,
        checkin_wfh_seen=rnd.random() < 0.15)
    c = classify_day(e)
    full, half = rnd.choice([(480, 240), (420, 210), (1, 0)])
    return {
        "evidence": {k: (v.value if isinstance(v, WorkMode) else v) for k, v in e.__dict__.items()},
        "full_day_minutes": full, "half_day_minutes": half,
        "class": {"work_mode": c.work_mode.value if c.work_mode else None, "source": c.source.value if c.source else None,
                  "office_minutes": c.office_minutes, "wfh_minutes": c.wfh_minutes, "conflicts": list(c.conflicts),
                  "hours_known": c.hours_known, "day_status": day_status(c, full, half).value},
    }


def wfo_case() -> dict:
    start = rdate(date(2026, 1, 1), date(2026, 10, 1))
    start = start.replace(day=1) if rnd.random() < 0.6 else start
    end = start + timedelta(days=rnd.choice([0, 6, 27, 29, 30, 45, 61, 90]))
    today = rdate(start - timedelta(days=10), end + timedelta(days=15))
    weekdays = set(rnd.sample(range(1, 8), rnd.randint(0, 6)))
    joined = rdate(start - timedelta(days=40), end) if rnd.random() < 0.3 else date(2000, 1, 1)
    is_scheduled = lambda d: d >= joined and d.isoweekday() in weekdays  # noqa: E731
    days = []
    d = start - timedelta(days=6)
    while d <= end + timedelta(days=6):
        if rnd.random() < 0.95:
            sched = is_scheduled(d) and rnd.random() < 0.9
            days.append(PolicyDay(d, rnd.choice([None, None, *MODES]), rnd.choice([0, 0, 100, 240, 480]), sched,
                                  rnd.random() < 0.8))
        d += timedelta(days=1)
    policy = WfoPolicy(rnd.choice([0, 2, 3, 5]), rnd.choice([0, 8, 12, 20]), rnd.choice([0, 240, 300]))
    s = summarize_wfo(days, start=start, end=end, today=today, policy=policy, is_scheduled=is_scheduled)
    return {
        "start": d2s(start), "end": d2s(end), "today": d2s(today), "weekdays": sorted(weekdays), "joined": d2s(joined),
        "policy": {"min_days_per_week": policy.min_days_per_week, "min_days_per_month": policy.min_days_per_month,
                   "min_office_minutes": policy.min_office_minutes},
        "days": [{"date": d2s(p.date), "work_mode": p.work_mode.value if p.work_mode else None,
                  "office_minutes": p.office_minutes, "scheduled": p.scheduled, "hours_known": p.hours_known} for p in days],
        "summary": {k: getattr(s, k) for k in (
            "working_days", "standard_days", "wfo_days", "wfh_days", "office_days_below_minimum", "leave_days",
            "holiday_days", "weekoff_days", "required_days", "shortfall_days", "wfo_percentage", "compliance")},
        "weeks": [{"week_start": d2s(w.week_start), "week_end": d2s(w.week_end), "label": w.label,
                   "working_days": w.working_days, "wfo_days": w.wfo_days, "required": w.required, "status": w.status,
                   "shortfall": w.shortfall} for w in s.weeks],
    }


def rounding_cases() -> list:
    out = []
    for _ in range(3000):
        a, b = rnd.randint(0, 400), rnd.randint(1, 400)
        x = a * 100 / b
        y = rnd.randint(0, 31) * rnd.randint(0, 40) / rnd.randint(1, 31)
        out.append({"present": a, "counted": b, "rate": attendance_rate(a, b), "x": x, "round1": round(x, 1),
                    "round0": round(a / b), "y": y, "half_up": round_half_up(y)})
    for x in (0.125, 0.375, 2.675, 1.005, 66.65, 0.05, 2.5, 3.5, 12.25, 12.35, 1e-7, 123456.789):
        out.append({"present": 0, "counted": 1, "rate": 0.0, "x": x, "round1": round(x, 1), "round0": round(x),
                    "y": x, "half_up": round_half_up(x)})
    return out


OUT.mkdir(parents=True, exist_ok=True)
compact = {"separators": (",", ":")}


def save(name: str, data) -> None:
    with gzip.open(OUT / f"{name}.json.gz", "wt", encoding="utf-8", compresslevel=9) as f:
        f.write(json.dumps(data, **compact))


save("rules-attendance", [attendance_case(i) for i in range(300)])
save("rules-evidence", [evidence_case() for _ in range(1500)])
save("rules-wfo", [wfo_case() for _ in range(300)])
save("rules-rounding", rounding_cases())
print("written:", ", ".join(f.name for f in OUT.glob("rules-*.json.gz")))
