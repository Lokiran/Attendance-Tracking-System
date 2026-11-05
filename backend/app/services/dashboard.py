"""Team / department / organisation analytics, all derived from day records of the viewer's scope."""

from collections import defaultdict
from collections.abc import Iterable
from dataclasses import dataclass
from datetime import date, timedelta

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.timeutil import local_date, office_tz
from app.models import AttendanceCorrection, DayStatus, Department, LeaveRecord, RequestStatus, Role, User
from app.services.attendance_data import compute_records
from app.services.attendance_rules import (
    EXC_LATE_ARRIVAL,
    EXC_MISSING_CHECKOUT,
    EXC_SHORT_HOURS,
    DayRecord,
    Summary,
    summarize,
)
from app.services.scope import resolve_scope

SNAPSHOT_KEYS = ("present", "absent", "not_checked_in", "on_leave", "incomplete", "off")
_SNAPSHOT_OF = {
    DayStatus.PRESENT: "present", DayStatus.ABSENT: "absent", DayStatus.NOT_YET_CHECKED_IN: "not_checked_in",
    DayStatus.ON_LEAVE: "on_leave", DayStatus.INCOMPLETE: "incomplete", DayStatus.HOLIDAY: "off",
    DayStatus.WEEKEND: "off", DayStatus.NOT_SCHEDULED: "off",
}


@dataclass
class ScopeArgs:
    manager_id: int | None = None
    department_id: int | None = None


def aggregate(summaries: list[Summary]) -> dict:
    expected = sum(s.expected_days for s in summaries)
    present_expected = sum(s.present_expected_days for s in summaries)
    attended = sum(s.attended_days_for_average for s in summaries)
    net = sum(s.net_minutes for s in summaries)
    required = sum(s.required_minutes for s in summaries)
    return {
        "expected_days": expected,
        "present_expected_days": present_expected,
        "present_days": sum(s.present_days for s in summaries),
        "absent_days": sum(s.absent_days for s in summaries),
        "leave_days": sum(s.leave_days for s in summaries),
        "incomplete_days": sum(s.incomplete_days for s in summaries),
        "late_days": sum(s.late_days for s in summaries),
        "unresolved_sessions": sum(s.unresolved_sessions for s in summaries),
        "net_minutes": net,
        "required_minutes": required,
        "variance_minutes": net - required,
        "attended_days_for_average": attended,
        "average_net_minutes": round(net / attended) if attended else None,
        "attendance_percentage": round(present_expected * 100 / expected, 1) if expected else None,
    }


def _snapshot(records_on_date: list[DayRecord | None]) -> dict:
    counts = dict.fromkeys(SNAPSHOT_KEYS, 0)
    active = 0
    for r in records_on_date:
        if r is None:
            continue
        counts[_SNAPSHOT_OF[r.status]] += 1
        active += int(r.has_active_session)
    counts["checked_in_now"] = active
    return counts


def _compute(db: Session, employees: list[User], start: date, end: date, on_date: date | None):
    lo = min(start, on_date) if on_date else start
    hi = max(end, on_date) if on_date else end
    all_records = compute_records(db, employees, lo, hi)
    period = {eid: [r for r in recs if start <= r.date <= end] for eid, recs in all_records.items()}
    on = {eid: next((r for r in recs if r.date == on_date), None) for eid, recs in all_records.items()} \
        if on_date else {}
    return period, on


def _pending_counts(db: Session, employee_ids: list[int]) -> dict:
    if not employee_ids:
        return {"pending_corrections": 0, "pending_leave": 0}
    pc = db.scalar(select(func.count()).select_from(AttendanceCorrection).where(
        AttendanceCorrection.employee_id.in_(employee_ids), AttendanceCorrection.status == RequestStatus.PENDING))
    pl = db.scalar(select(func.count()).select_from(LeaveRecord).where(
        LeaveRecord.employee_id.in_(employee_ids), LeaveRecord.status == RequestStatus.PENDING))
    return {"pending_corrections": pc, "pending_leave": pl}


def overview(db: Session, viewer: User, scope: ScopeArgs, start: date, end: date, on_date: date) -> dict:
    employees = resolve_scope(db, viewer, manager_id=scope.manager_id, department_id=scope.department_id)
    period, on = _compute(db, employees, start, end, on_date)
    summaries = [summarize(period[e.id], start, end) for e in employees]
    return {
        "generated_at": clock.utcnow(),
        "on_date": on_date,
        "start": start,
        "end": end,
        "headcount": len(employees),
        "snapshot": _snapshot([on.get(e.id) for e in employees]),
        "period": aggregate(summaries),
        **_pending_counts(db, [e.id for e in employees]),
    }


SORT_KEYS = {
    "name": lambda r: r["full_name"].lower(),
    "code": lambda r: r["employee_code"],
    "department": lambda r: (r["department_name"] or "").lower(),
    "check_in": lambda r: (r["day"]["first_check_in"] is None, r["day"]["first_check_in"] or 0),
    "net_minutes": lambda r: r["period"]["net_minutes"],
    "attendance_percentage": lambda r: (r["period"]["attendance_percentage"] is None,
                                        r["period"]["attendance_percentage"] or 0),
    "status": lambda r: r["day"]["status"] or "",
    "exceptions": lambda r: r["exception_count"],
}


def employee_rows(
    db: Session, viewer: User, scope: ScopeArgs, start: date, end: date, on_date: date, *,
    search: str | None = None, status: DayStatus | None = None, sort: str = "name", order: str = "asc",
    page: int = 1, page_size: int = 20, include_inactive: bool = False,
) -> dict:
    employees = resolve_scope(db, viewer, manager_id=scope.manager_id, department_id=scope.department_id,
                              include_inactive=include_inactive)
    if search:
        q = search.strip().lower()
        employees = [e for e in employees if q in e.full_name.lower() or q in e.employee_code.lower()]
    period, on = _compute(db, employees, start, end, on_date)

    rows = []
    for e in employees:
        day = on.get(e.id)
        if status is not None and (day is None or day.status != status):
            continue
        summary = summarize(period[e.id], start, end)
        exc_count = sum(len(r.exceptions) for r in period[e.id])
        rows.append({
            "employee_id": e.id, "employee_code": e.employee_code, "full_name": e.full_name,
            "job_title": e.job_title, "role": e.role, "is_active": e.is_active,
            "department_id": e.department_id, "department_name": e.department.name if e.department else None,
            "manager_id": e.manager_id, "manager_name": e.manager.full_name if e.manager else None,
            "day": {
                "status": day.status if day else None,
                "first_check_in": day.first_check_in if day else None,
                "last_check_out": day.last_check_out if day else None,
                "net_minutes": day.net_minutes if day else 0,
                "has_active_session": day.has_active_session if day else False,
                "exceptions": day.exceptions if day else [],
            },
            "period": aggregate([summary]),
            "exception_count": exc_count,
        })
    key = SORT_KEYS.get(sort, SORT_KEYS["name"])
    rows.sort(key=key, reverse=(order == "desc"))
    total = len(rows)
    offset = (page - 1) * page_size
    return {"items": rows[offset:offset + page_size], "total": total, "page": page, "page_size": page_size}


def _bucket_key(d: date, granularity: str) -> tuple[date, date, str]:
    if granularity == "week":
        s = d - timedelta(days=d.weekday())
        return s, s + timedelta(days=6), f"Wk {s.isocalendar().week} ({s:%d %b})"
    if granularity == "month":
        s = d.replace(day=1)
        nxt = (s.replace(day=28) + timedelta(days=4)).replace(day=1)
        return s, nxt - timedelta(days=1), f"{s:%b %Y}"
    return d, d, f"{d:%d %b}"


def bucketize(records: Iterable[DayRecord], end: date, granularity: str) -> list[dict]:
    """Aggregate day records into day/week/month buckets (shared by team and employee trends)."""
    buckets: dict[date, dict] = {}
    for r in records:
        b_start, b_end, label = _bucket_key(r.date, granularity)
        b = buckets.setdefault(b_start, {
            "start": b_start, "end": min(b_end, end), "label": label, "expected": 0, "present_expected": 0,
            "present": 0, "absent": 0, "on_leave": 0, "incomplete": 0, "net_minutes": 0, "attended": 0,
            "required_minutes": 0, "late": 0, "missing_checkout": 0, "short_hours": 0,
        })
        b["expected"] += int(r.expected)
        b["present_expected"] += int(r.expected and r.status == DayStatus.PRESENT)
        b["present"] += int(r.status == DayStatus.PRESENT)
        b["absent"] += int(r.status == DayStatus.ABSENT)
        b["on_leave"] += int(r.status == DayStatus.ON_LEAVE)
        b["incomplete"] += int(r.status == DayStatus.INCOMPLETE)
        b["net_minutes"] += r.net_minutes
        b["required_minutes"] += r.required_minutes
        b["attended"] += int(r.status == DayStatus.PRESENT and r.net_minutes > 0)
        b["late"] += int(EXC_LATE_ARRIVAL in r.exceptions)
        b["missing_checkout"] += int(EXC_MISSING_CHECKOUT in r.exceptions)
        b["short_hours"] += int(EXC_SHORT_HOURS in r.exceptions)
    items = []
    for b in sorted(buckets.values(), key=lambda x: x["start"]):
        b["attendance_percentage"] = round(b["present_expected"] * 100 / b["expected"], 1) if b["expected"] else None
        b["average_net_minutes"] = round(b["net_minutes"] / b["attended"]) if b["attended"] else None
        items.append(b)
    return items


def trends(db: Session, viewer: User, scope: ScopeArgs, start: date, end: date, granularity: str) -> dict:
    employees = resolve_scope(db, viewer, manager_id=scope.manager_id, department_id=scope.department_id)
    period, _ = _compute(db, employees, start, end, None)
    records = (r for recs in period.values() for r in recs)
    return {"granularity": granularity, "headcount": len(employees), "items": bucketize(records, end, granularity)}


def exceptions(db: Session, viewer: User, scope: ScopeArgs, start: date, end: date, *,
               exception_type: str | None = None, page: int = 1, page_size: int = 20) -> dict:
    employees = resolve_scope(db, viewer, manager_id=scope.manager_id, department_id=scope.department_id)
    period, _ = _compute(db, employees, start, end, None)
    by_id = {e.id: e for e in employees}
    counts: dict[str, int] = defaultdict(int)
    rows = []
    for eid, recs in period.items():
        e = by_id[eid]
        for r in recs:
            for x in r.exceptions:
                counts[x] += 1
            if not r.exceptions or (exception_type and exception_type not in r.exceptions):
                continue
            rows.append({
                "employee_id": e.id, "employee_code": e.employee_code, "full_name": e.full_name,
                "department_name": e.department.name if e.department else None, "date": r.date,
                "status": r.status, "exceptions": r.exceptions, "first_check_in": r.first_check_in,
                "last_check_out": r.last_check_out, "net_minutes": r.net_minutes,
                "required_minutes": r.required_minutes,
            })
    rows.sort(key=lambda x: (x["date"], x["full_name"]), reverse=True)
    offset = (page - 1) * page_size
    return {"counts": dict(counts), "items": rows[offset:offset + page_size], "total": len(rows), "page": page,
            "page_size": page_size}


def _reports_tree(users: list[User]) -> dict[int, set[int]]:
    children: dict[int, list[int]] = defaultdict(list)
    for u in users:
        if u.manager_id is not None:
            children[u.manager_id].append(u.id)

    def collect(root: int) -> set[int]:
        out: set[int] = set()
        stack = list(children.get(root, []))
        while stack:
            n = stack.pop()
            if n in out or n == root:
                continue
            out.add(n)
            stack.extend(children.get(n, []))
        return out

    return {u.id: collect(u.id) for u in users if u.id in children}


def department_comparison(db: Session, viewer: User, start: date, end: date, on_date: date) -> list[dict]:
    employees = resolve_scope(db, viewer)
    period, on = _compute(db, employees, start, end, on_date)
    departments = list(db.scalars(select(Department).order_by(Department.name)))
    out = []
    for dept in [*departments, None]:
        members = [e for e in employees if e.department_id == (dept.id if dept else None)]
        if dept is None and not members:
            continue
        out.append({
            "department_id": dept.id if dept else None,
            "department_name": dept.name if dept else "Unassigned",
            "head_name": dept.head.full_name if dept and dept.head else None,
            "headcount": len(members),
            "snapshot": _snapshot([on.get(e.id) for e in members]),
            "period": aggregate([summarize(period[e.id], start, end) for e in members]),
        })
    return out


def manager_comparison(db: Session, viewer: User, start: date, end: date, on_date: date,
                       department_id: int | None = None) -> list[dict]:
    employees = resolve_scope(db, viewer)
    all_users = list(db.scalars(select(User)).unique())
    tree = _reports_tree(all_users)
    period, on = _compute(db, employees, start, end, on_date)
    scoped = {e.id: e for e in employees}
    out = []
    for u in all_users:
        if u.id not in tree or u.role == Role.EMPLOYEE:
            continue
        if department_id is not None and u.department_id != department_id:
            continue
        members = [scoped[i] for i in tree[u.id] if i in scoped]
        out.append({
            "manager_id": u.id, "manager_name": u.full_name, "employee_code": u.employee_code,
            "department_name": u.department.name if u.department else None,
            "team_size": len(members),
            "direct_reports": sum(1 for m in members if m.manager_id == u.id),
            "snapshot": _snapshot([on.get(m.id) for m in members]),
            "period": aggregate([summarize(period[m.id], start, end) for m in members]),
        })
    out.sort(key=lambda r: r["manager_name"])
    return out


def today() -> date:
    return local_date(clock.utcnow(), office_tz())
