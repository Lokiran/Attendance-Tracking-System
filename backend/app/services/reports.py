"""CSV report generation. Every report reuses the same scope rules as the dashboards."""

import calendar
import csv
import io
from collections.abc import Iterable, Sequence
from datetime import date, datetime

from sqlalchemy.orm import Session

from app.core.timeutil import office_tz
from app.models import User
from app.services import dashboard
from app.services.attendance_data import compute_records
from app.services.attendance_rules import summarize
from app.services.scope import get_viewable_employee, resolve_scope

_FORMULA_PREFIXES = ("=", "+", "-", "@", "\t", "\r", "\n")


def safe_cell(value) -> str:
    """Neutralise spreadsheet formula injection (CWE-1236) by prefixing risky text with an apostrophe."""
    if value is None:
        return ""
    if isinstance(value, bool):
        return "Yes" if value else "No"
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, datetime):
        return value.astimezone(office_tz()).strftime("%Y-%m-%d %H:%M")
    if isinstance(value, date):
        return value.isoformat()
    text = str(value)
    if text.startswith(_FORMULA_PREFIXES):
        return "'" + text
    return text


def to_csv(header: Sequence[str], rows: Iterable[Sequence]) -> str:
    buf = io.StringIO()
    writer = csv.writer(buf, quoting=csv.QUOTE_MINIMAL, lineterminator="\r\n")
    writer.writerow(header)
    for row in rows:
        writer.writerow([safe_cell(v) for v in row])
    return "﻿" + buf.getvalue()  # BOM so Excel detects UTF-8


def _hours(minutes: int | None) -> str:
    return "" if minutes is None else f"{minutes / 60:.2f}"


def employee_attendance(db: Session, viewer: User, employee_id: int, start: date, end: date) -> tuple[str, str]:
    employee = get_viewable_employee(db, viewer, employee_id)
    records = compute_records(db, [employee], start, end)[employee.id]
    rows = [
        (employee.employee_code, employee.full_name, r.date, r.status.value, r.first_check_in, r.last_check_out,
         _hours(r.net_minutes), _hours(r.required_minutes), r.session_count, "; ".join(r.exceptions),
         r.holiday_name or r.leave_type or "")
        for r in records
    ]
    header = ("Employee ID", "Name", "Date", "Status", "First check-in", "Last check-out", "Net hours",
              "Required hours", "Sessions", "Exceptions", "Holiday / leave")
    return f"attendance_{employee.employee_code}_{start}_{end}.csv", to_csv(header, rows)


def _summary_rows(db: Session, employees: list[User], start: date, end: date):
    records = compute_records(db, employees, start, end)
    for e in employees:
        s = summarize(records[e.id], start, end)
        yield (e.employee_code, e.full_name, e.department.name if e.department else "",
               e.manager.full_name if e.manager else "", s.expected_days, s.present_expected_days,
               s.extra_present_days, s.absent_days, s.leave_days, s.incomplete_days, s.late_days,
               _hours(s.net_minutes), _hours(s.required_minutes), _hours(s.variance_minutes),
               _hours(s.average_net_minutes), "" if s.attendance_percentage is None else s.attendance_percentage)


SUMMARY_HEADER = ("Employee ID", "Name", "Department", "Manager", "Expected days", "Present (expected) days",
                  "Extra days worked", "Absent days", "Leave days", "Incomplete days", "Late days", "Net hours",
                  "Required hours", "Variance hours", "Avg net hours / day", "Attendance %")


def team_attendance(db: Session, viewer: User, start: date, end: date, manager_id: int | None,
                    department_id: int | None) -> tuple[str, str]:
    employees = resolve_scope(db, viewer, manager_id=manager_id, department_id=department_id)
    return f"team_attendance_{start}_{end}.csv", to_csv(SUMMARY_HEADER, _summary_rows(db, employees, start, end))


def monthly_attendance(db: Session, viewer: User, year: int, month: int, manager_id: int | None,
                       department_id: int | None) -> tuple[str, str]:
    start = date(year, month, 1)
    end = date(year, month, calendar.monthrange(year, month)[1])
    employees = resolve_scope(db, viewer, manager_id=manager_id, department_id=department_id)
    header = ("Month", *SUMMARY_HEADER)
    rows = ((f"{year}-{month:02d}", *row) for row in _summary_rows(db, employees, start, end))
    return f"monthly_attendance_{year}-{month:02d}.csv", to_csv(header, rows)


def department_attendance(db: Session, viewer: User, start: date, end: date) -> tuple[str, str]:
    data = dashboard.department_comparison(db, viewer, start, end, end)
    header = ("Department", "Head", "Headcount", "Expected days", "Present (expected) days", "Absent days",
              "Leave days", "Incomplete days", "Net hours", "Required hours", "Avg net hours / day", "Attendance %")
    rows = [
        (d["department_name"], d["head_name"], d["headcount"], d["period"]["expected_days"],
         d["period"]["present_expected_days"], d["period"]["absent_days"], d["period"]["leave_days"],
         d["period"]["incomplete_days"], _hours(d["period"]["net_minutes"]), _hours(d["period"]["required_minutes"]),
         _hours(d["period"]["average_net_minutes"]),
         "" if d["period"]["attendance_percentage"] is None else d["period"]["attendance_percentage"])
        for d in data
    ]
    return f"department_attendance_{start}_{end}.csv", to_csv(header, rows)


def exceptions_report(db: Session, viewer: User, start: date, end: date, manager_id: int | None,
                      department_id: int | None) -> tuple[str, str]:
    data = dashboard.exceptions(db, viewer, dashboard.ScopeArgs(manager_id, department_id), start, end,
                                page=1, page_size=100_000)
    header = ("Date", "Employee ID", "Name", "Department", "Status", "Exceptions", "First check-in",
              "Last check-out", "Net hours", "Required hours")
    rows = [
        (r["date"], r["employee_code"], r["full_name"], r["department_name"], r["status"].value,
         "; ".join(r["exceptions"]), r["first_check_in"], r["last_check_out"], _hours(r["net_minutes"]),
         _hours(r["required_minutes"]))
        for r in data["items"]
    ]
    return f"attendance_exceptions_{start}_{end}.csv", to_csv(header, rows)
