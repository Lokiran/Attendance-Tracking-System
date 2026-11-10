"""WFO tracking: collect each employee-day's evidence from the database, classify it with the pure rules
(``wfo_rules``), store the result in ``attendance_daily`` and build monthly policy summaries.

Reads always recompute from the evidence, so they are never stale; ``refresh_days`` writes the result
back whenever a source changes (a committed timesheet upload, an Admin override). Check-in sessions are
no longer a source (attendance comes from imported timesheets).
"""

from __future__ import annotations

import calendar
import re
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.context import RequestContext
from app.core.errors import Forbidden, NotFound, ValidationFailed
from app.core.timeutil import local_date, office_tz
from app.models import (
    AttendanceDaily,
    AttendancePolicy,
    DailyStatus,
    User,
    WorkMode,
)
from app.services import audit
from app.services.attendance_data import _load_rules_inputs, employee_data
from app.services.attendance_rules import resolve_schedule
from app.services.policy import get_policy
from app.services.scope import resolve_scope
from app.services.wfo_rules import (
    DayClass,
    DayEvidence,
    PolicyDay,
    WfoPolicy,
    WfoSummary,
    classify_day,
    day_status,
    is_wfo_day,
    summarize_wfo,
    wfo_band,
)


def policy_of(p: AttendancePolicy) -> WfoPolicy:
    return WfoPolicy(min_days_per_week=p.min_wfo_days_per_week, min_days_per_month=p.min_wfo_days_per_month,
                     min_office_minutes=p.min_office_minutes_for_wfo_day)


def month_bounds(month: str | None, today: date) -> tuple[str, date, date]:
    """``YYYY-MM`` (default: the current month) -> (label, first day, last day)."""
    if not month:
        y, m = today.year, today.month
    else:
        match = re.fullmatch(r"(\d{4})-(\d{2})", month.strip())
        if not match or not 1 <= int(match[2]) <= 12 or not 2000 <= int(match[1]) <= 2100:
            raise ValidationFailed("month must look like YYYY-MM")
        y, m = int(match[1]), int(match[2])
    start = date(y, m, 1)
    return f"{y:04d}-{m:02d}", start, date(y, m, calendar.monthrange(y, m)[1])


@dataclass
class ClassifiedDay:
    date: date
    cls: DayClass
    scheduled: bool                      # scheduled working day, not a holiday
    evidence: DayEvidence
    row: AttendanceDaily | None
    first_in: datetime | None = None
    last_out: datetime | None = None


@dataclass
class Calendar:
    """Schedule and holiday facts for one employee, reused for the nominal-week calculation."""
    schedules: list
    holidays: dict[date, str]
    employee: object

    def scheduled(self, d: date) -> bool:
        if d < self.employee.date_joined:
            return False
        s = resolve_schedule(self.schedules, self.employee, d)
        return s is not None and d.isoweekday() in s.working_days


@dataclass
class EmployeeDays:
    employee: User
    days: list[ClassifiedDay]
    calendar: Calendar
    rows_by_date: dict[date, AttendanceDaily] = field(default_factory=dict)


def classify_range(db: Session, employees: list[User], start: date, end: date,
                   overrides: dict[tuple[int, date], dict] | None = None) -> dict[int, EmployeeDays]:
    """Classified days ``start..end`` per employee (future days included, marked by date).

    ``overrides`` replaces evidence fields for (employee, date) pairs; the upload preview uses it to
    show what a file would change without writing anything.
    """
    ids = [e.id for e in employees]
    # Six days either side so nominal weeks crossing the month boundary can be sized.
    schedules, holidays, _policy = _load_rules_inputs(db, start - timedelta(days=6), end + timedelta(days=6))
    rows: dict[tuple[int, date], AttendanceDaily] = {}
    for i in range(0, len(ids), 500):
        chunk = ids[i:i + 500]
        for r in db.scalars(select(AttendanceDaily).where(AttendanceDaily.employee_id.in_(chunk),
                                                          AttendanceDaily.work_date.between(start, end))):
            rows[(r.employee_id, r.work_date)] = r

    out: dict[int, EmployeeDays] = {}
    for e in employees:
        emp = employee_data(e)
        holiday_map = {h.holiday_date: h.name for h in holidays
                       if h.department_id is None or h.department_id == e.department_id}
        cal = Calendar(schedules, holiday_map, emp)
        days: list[ClassifiedDay] = []
        d = start
        while d <= end:
            row = rows.get((e.id, d))
            workday = cal.scheduled(d)
            holiday = workday and d in holiday_map
            fields = {
                "scheduled_workday": workday, "holiday": holiday,
                "override": row.override_work_mode if row else None,
                "upload_mode": row.upload_work_mode if row else None,
                "upload_minutes": row.upload_minutes if row else None,
                "badge_minutes": row.badge_minutes if row else None,
                "wifi_minutes": row.wifi_minutes if row else None,
            }
            if overrides and (e.id, d) in overrides:
                fields.update(overrides[(e.id, d)])
            evidence = DayEvidence(**fields)
            cls = classify_day(evidence) if d >= emp.date_joined else DayClass(None, None, 0, 0)
            days.append(ClassifiedDay(d, cls, workday and not holiday, evidence, row,
                                      first_in=row.first_in if row else None, last_out=row.last_out if row else None))
            d += timedelta(days=1)
        out[e.id] = EmployeeDays(e, days, cal, {k[1]: v for k, v in rows.items() if k[0] == e.id})
    return out


WEEK_PAD = timedelta(days=6)  # also classify the neighbouring days of the first and last week


def summarize(ed: EmployeeDays, start: date, end: date, today: date, policy: WfoPolicy) -> WfoSummary:
    days = [PolicyDay(c.date, c.cls.work_mode, c.cls.office_minutes, c.scheduled, c.cls.hours_known) for c in ed.days]
    return summarize_wfo(days, start=start, end=end, today=today, policy=policy, is_scheduled=ed.calendar.scheduled)


def _has_evidence(c: ClassifiedDay) -> bool:
    e = c.evidence
    return bool(c.row or e.override or e.upload_mode or e.upload_minutes or e.badge_minutes is not None
                or e.wifi_minutes)


def store(db: Session, ed: EmployeeDays, policy_row: AttendancePolicy, only: set[date] | None = None) -> int:
    """Write classification results into attendance_daily (creating rows only for days with evidence)."""
    now = clock.utcnow()
    written = 0
    for c in ed.days:
        if only is not None and c.date not in only:
            continue
        if not _has_evidence(c):
            continue
        row = c.row or ed.rows_by_date.get(c.date)
        if row is None:
            row = AttendanceDaily(employee_id=ed.employee.id, work_date=c.date, office_minutes=0, wfh_minutes=0,
                                  corrected=False, conflicts=[])
            db.add(row)
            ed.rows_by_date[c.date] = row
        row.work_mode = c.cls.work_mode
        row.wfo_source = c.cls.source
        row.conflicts = list(c.cls.conflicts)
        row.office_minutes = c.cls.office_minutes
        row.wfh_minutes = c.cls.wfh_minutes
        row.status = day_status(c.cls, policy_row.full_day_minutes, policy_row.half_day_minutes)
        if c.first_in and c.last_out and c.last_out >= c.first_in:
            row.first_in, row.last_out = c.first_in, c.last_out
        elif c.first_in:
            row.first_in, row.last_out = c.first_in, None
        row.computed_at = now
        written += 1
    return written


def refresh_days(db: Session, employee: User, dates: set[date]) -> None:
    """Recompute and store the given days for one employee (caller commits)."""
    if not dates:
        return
    db.flush()
    ed = classify_range(db, [employee], min(dates), max(dates))[employee.id]
    store(db, ed, get_policy(db), only=dates)


def refresh_range(db: Session, employees: list[User], start: date, end: date) -> int:
    """Backfill (seed, CLI). Caller commits."""
    policy_row = get_policy(db)
    total = 0
    for i in range(0, len(employees), 200):
        for ed in classify_range(db, employees[i:i + 200], start, end).values():
            total += store(db, ed, policy_row)
    return total


# ------------------------------------------------------------------ API-shaped results

def employee_brief(u: User) -> dict:
    return {"id": u.id, "employee_code": u.employee_code, "full_name": u.full_name,
            "department_name": u.department.name if u.department else None,
            "manager_id": u.manager_id, "manager_name": u.manager.full_name if u.manager else None}


def summary_dict(s: WfoSummary) -> dict:
    return {
        "working_days": s.working_days, "standard_days": s.standard_days, "wfo_days": s.wfo_days,
        "wfh_days": s.wfh_days, "office_days_below_minimum": s.office_days_below_minimum,
        "leave_days": s.leave_days, "holiday_days": s.holiday_days, "weekoff_days": s.weekoff_days,
        "required_days": s.required_days, "shortfall_days": s.shortfall_days, "wfo_percentage": s.wfo_percentage,
        "compliance": s.compliance, "wfo_band": wfo_band(s.wfo_days, s.required_days),
    }


def weeks_list(s: WfoSummary) -> list[dict]:
    return [{"week_start": w.week_start, "week_end": w.week_end, "label": w.label, "working_days": w.working_days,
             "wfo_days": w.wfo_days, "required": w.required, "status": w.status, "shortfall": w.shortfall}
            for w in s.weeks]


def policy_dict(p: WfoPolicy) -> dict:
    return {"min_days_per_week": p.min_days_per_week, "min_days_per_month": p.min_days_per_month,
            "min_office_minutes": p.min_office_minutes}


def employee_month(db: Session, employee: User, month: str | None) -> dict:
    today = local_date(clock.utcnow(), office_tz())
    label, start, end = month_bounds(month, today)
    policy = policy_of(get_policy(db))
    ed = classify_range(db, [employee], start - WEEK_PAD, end + WEEK_PAD)[employee.id]
    s = summarize(ed, start, end, today, policy)
    return {
        "employee": employee_brief(employee), "month": label, "start": start, "end": end, "today": today,
        "policy": policy_dict(policy), "summary": summary_dict(s), "weekly_wfo_breakdown": weeks_list(s),
        "days": [{
            "date": c.date, "work_mode": c.cls.work_mode, "wfo_source": c.cls.source,
            "is_wfo_day": is_wfo_day(PolicyDay(c.date, c.cls.work_mode, c.cls.office_minutes, c.scheduled, c.cls.hours_known), policy),
            "office_minutes": c.cls.office_minutes, "wfh_minutes": c.cls.wfh_minutes,
            "conflicts": list(c.cls.conflicts), "scheduled": c.scheduled,
            "overridden": c.evidence.override is not None,
            "override_note": c.row.override_note if c.row else None,
            "in_progress": c.date == today, "future": c.date > today,
        } for c in ed.days if start <= c.date <= end],
    }


def range_summary(db: Session, employee: User, start: date, end: date) -> dict:
    """WFO figures for an arbitrary range (added to the existing attendance summary)."""
    today = local_date(clock.utcnow(), office_tz())
    policy = policy_of(get_policy(db))
    ed = classify_range(db, [employee], start - WEEK_PAD, end + WEEK_PAD)[employee.id]
    s = summarize(ed, start, end, today, policy)
    return {**summary_dict(s), "weekly_wfo_breakdown": weeks_list(s)}


def rows_for(db: Session, employees: list[User], start: date, end: date, today: date,
             policy: WfoPolicy) -> list[dict]:
    rows = []
    for i in range(0, len(employees), 200):
        chunk = employees[i:i + 200]
        classified = classify_range(db, chunk, start - WEEK_PAD, end + WEEK_PAD)
        for e in chunk:
            s = summarize(classified[e.id], start, end, today, policy)
            rows.append({"employee": employee_brief(e), **summary_dict(s)})
    return rows


def totals(rows: list[dict]) -> dict:
    working = sum(r["working_days"] for r in rows)
    wfo = sum(r["wfo_days"] for r in rows)
    return {
        "headcount": len(rows), "working_days": working, "wfo_days": wfo,
        "wfh_days": sum(r["wfh_days"] for r in rows),
        "wfo_percentage": round(wfo * 100 / working, 1) if working else None,
        "compliant": sum(1 for r in rows if r["compliance"] == "COMPLIANT"),
        "non_compliant": sum(1 for r in rows if r["compliance"] == "NON_COMPLIANT"),
        # People per WFO band (monthly WFO days against the requirement): green / orange / red.
        "band_met": sum(1 for r in rows if r["wfo_band"] == "MET"),
        "band_near": sum(1 for r in rows if r["wfo_band"] == "NEAR"),
        "band_below": sum(1 for r in rows if r["wfo_band"] == "BELOW"),
    }


# ------------------------------------------------------------------ scopes (team / org)

def team_scope(db: Session, viewer: User) -> list[User]:
    """Manager: their whole reporting line plus themselves; admin: everyone tracked."""
    people = resolve_scope(db, viewer)
    if viewer.attendance_tracked and viewer.is_active and all(p.id != viewer.id for p in people):
        people = [viewer, *people]
    return people


def team_month(db: Session, viewer: User, month: str | None, employee_id: int | None = None) -> dict:
    today = local_date(clock.utcnow(), office_tz())
    label, start, end = month_bounds(month, today)
    policy = policy_of(get_policy(db))
    people = team_scope(db, viewer)
    if employee_id is not None:
        people = [p for p in people if p.id == employee_id]
        if not people:
            raise Forbidden("That employee is not in your team")
    rows = rows_for(db, people, start, end, today, policy)
    return {"month": label, "start": start, "end": end, "policy": policy_dict(policy), "totals": totals(rows),
            "rows": rows}


def scoped_employee(db: Session, viewer: User, employee_id: int) -> User:
    """An employee the viewer may see WFO for; 403 (not 404) for anyone else, as the WFO API specifies."""
    for p in team_scope(db, viewer):
        if p.id == employee_id:
            return p
    raise Forbidden("That employee is not in your team")


def org_month(db: Session, admin: User, month: str | None, manager_id: int | None = None,
              department_id: int | None = None) -> dict:
    today = local_date(clock.utcnow(), office_tz())
    label, start, end = month_bounds(month, today)
    policy = policy_of(get_policy(db))
    people = resolve_scope(db, admin, manager_id=manager_id, department_id=department_id)
    rows = rows_for(db, people, start, end, today, policy)
    groups: dict[int | None, list[dict]] = defaultdict(list)
    for r in rows:
        groups[r["employee"]["manager_id"]].append(r)
    managers = {u.id: u for u in db.scalars(select(User).where(User.id.in_([k for k in groups if k]))).unique()} \
        if any(groups) else {}
    out = []
    for mid, members in groups.items():
        m = managers.get(mid) if mid else None
        out.append({"manager_id": mid, "manager_name": m.full_name if m else "No manager",
                    "department_name": m.department.name if m and m.department else None,
                    "totals": totals(members), "rows": sorted(members, key=lambda r: r["employee"]["full_name"])})
    out.sort(key=lambda g: (g["manager_id"] is None, g["manager_name"]))
    return {"month": label, "start": start, "end": end, "policy": policy_dict(policy), "totals": totals(rows),
            "groups": out}


# ------------------------------------------------------------------ manual override and policy

def _override_snapshot(db: Session, employee: User, day: date) -> dict:
    """The day as it effectively is (freshly classified), for the audit log's old / new values."""
    db.flush()
    c = classify_range(db, [employee], day, day)[employee.id].days[0]
    return {"work_mode": c.cls.work_mode, "wfo_source": c.cls.source,
            "override_note": c.row.override_note if c.row else None}


def _check_override_date(employee: User, day: date) -> None:
    today = local_date(clock.utcnow(), office_tz())
    if day < employee.date_joined:
        raise ValidationFailed("That date is before the employee joined")
    if day > today + timedelta(days=366):
        raise ValidationFailed("Overrides can be set at most a year ahead")


def set_override(db: Session, admin: User, employee: User, day: date, mode: WorkMode, note: str | None,
                 ctx: RequestContext) -> AttendanceDaily:
    _check_override_date(employee, day)
    row = db.scalar(select(AttendanceDaily).where(AttendanceDaily.employee_id == employee.id,
                                                  AttendanceDaily.work_date == day))
    before = _override_snapshot(db, employee, day)
    if row is None:
        row = AttendanceDaily(employee_id=employee.id, work_date=day, office_minutes=0, wfh_minutes=0,
                              corrected=False, conflicts=[], status=DailyStatus.ABSENT)
        db.add(row)
    row.override_work_mode = mode
    row.override_note = (note or "").strip() or None
    row.overridden_by_id = admin.id
    row.overridden_at = clock.utcnow()
    refresh_days(db, employee, {day})
    audit.record(db, actor_id=admin.id, action="WORK_MODE_OVERRIDDEN", entity_type="attendance_daily",
                 entity_id=f"{employee.id}:{day.isoformat()}", before=before,
                 after=_override_snapshot(db, employee, day), ctx=ctx)
    db.commit()
    db.refresh(row)
    return row


def clear_override(db: Session, admin: User, employee: User, day: date, ctx: RequestContext) -> AttendanceDaily:
    row = db.scalar(select(AttendanceDaily).where(AttendanceDaily.employee_id == employee.id,
                                                  AttendanceDaily.work_date == day))
    if row is None or row.override_work_mode is None:
        raise NotFound("There is no override on that day")
    before = _override_snapshot(db, employee, day)
    row.override_work_mode = None
    row.override_note = None
    row.overridden_by_id = None
    row.overridden_at = None
    refresh_days(db, employee, {day})
    audit.record(db, actor_id=admin.id, action="WORK_MODE_OVERRIDE_CLEARED", entity_type="attendance_daily",
                 entity_id=f"{employee.id}:{day.isoformat()}", before=before,
                 after=_override_snapshot(db, employee, day), ctx=ctx)
    db.commit()
    db.refresh(row)
    return row


POLICY_FIELDS = {"min_days_per_week": "min_wfo_days_per_week", "min_days_per_month": "min_wfo_days_per_month",
                 "min_office_minutes": "min_office_minutes_for_wfo_day"}


def update_policy(db: Session, admin: User, data: dict, ctx: RequestContext) -> WfoPolicy:
    p = get_policy(db)
    before = policy_dict(policy_of(p))
    for key, column in POLICY_FIELDS.items():
        if data.get(key) is not None:
            setattr(p, column, data[key])
    p.updated_by_id = admin.id
    db.flush()
    b, a = audit.changes(before, policy_dict(policy_of(p)))
    if a:
        audit.record(db, actor_id=admin.id, action="WFO_POLICY_UPDATED", entity_type="attendance_policy", entity_id=1,
                     before=b, after=a, ctx=ctx)
    db.commit()
    return policy_of(p)
