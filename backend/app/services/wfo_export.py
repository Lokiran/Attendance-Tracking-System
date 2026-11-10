"""WFO report as an Excel workbook: Summary (one row per employee), Daily (one row per employee-day), Policy."""

from __future__ import annotations

import io
from datetime import date

from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill
from openpyxl.utils import get_column_letter
from sqlalchemy.orm import Session

from app.core import clock
from app.core.timeutil import local_date, office_tz
from app.models import User
from app.services import wfo
from app.services.policy import get_policy
from app.services.scope import resolve_scope
from app.services.wfo_rules import PolicyDay, is_wfo_day, wfo_band

HEADER_FILL = PatternFill("solid", fgColor="E8EDF5")  # the app's secondary-soft table header tint
RISKY = ("=", "+", "-", "@", "\t", "\r")


def _text(value):
    """Text cells that a spreadsheet could run as a formula get a leading apostrophe (CWE-1236)."""
    if isinstance(value, str) and value.startswith(RISKY):
        return "'" + value
    return value


def _sheet(wb: Workbook, title: str, header: list[str], rows: list[list], first: bool = False):
    ws = wb.active if first else wb.create_sheet()
    ws.title = title
    ws.append(header)
    for cell in ws[1]:
        cell.font = Font(bold=True)
        cell.fill = HEADER_FILL
    for row in rows:
        ws.append([_text(v) for v in row])
    for i, name in enumerate(header, start=1):
        width = max([len(str(name))] + [len(str(r[i - 1])) for r in rows[:500] if r[i - 1] is not None])
        ws.column_dimensions[get_column_letter(i)].width = min(48, width + 2)
    ws.freeze_panes = "A2"
    return ws


def build(db: Session, admin: User, month: str | None, manager_id: int | None, department_id: int | None
          ) -> tuple[str, bytes]:
    today = local_date(clock.utcnow(), office_tz())
    label, start, end = wfo.month_bounds(month, today)
    policy = wfo.policy_of(get_policy(db))
    people = resolve_scope(db, admin, manager_id=manager_id, department_id=department_id)
    summary_rows, daily_rows = [], []
    for i in range(0, len(people), 200):
        chunk = people[i:i + 200]
        classified = wfo.classify_range(db, chunk, start - wfo.WEEK_PAD, end + wfo.WEEK_PAD)
        for e in chunk:
            ed = classified[e.id]
            s = wfo.summarize(ed, start, end, today, policy)
            summary_rows.append([
                e.employee_code, e.full_name, e.department.name if e.department else "",
                e.manager.full_name if e.manager else "", s.working_days, s.wfo_days, s.wfh_days, s.leave_days,
                s.wfo_percentage if s.wfo_percentage is not None else "", s.required_days, s.shortfall_days,
                s.compliance, {"MET": "Met", "NEAR": "Close (up to 3 days short)", "BELOW": "Below"}[
                    wfo_band(s.wfo_days, s.required_days)],
            ])
            for c in ed.days:
                if c.date >= today or not start <= c.date <= end:
                    continue
                daily_rows.append([
                    e.employee_code, e.full_name, c.date,
                    c.cls.work_mode.value if c.cls.work_mode else "", c.cls.source.value if c.cls.source else "",
                    "Yes" if is_wfo_day(PolicyDay(c.date, c.cls.work_mode, c.cls.office_minutes, c.scheduled, c.cls.hours_known), policy)
                    else "No",
                    round(c.cls.office_minutes / 60, 2), round(c.cls.wfh_minutes / 60, 2), "; ".join(c.cls.conflicts),
                ])
    wb = Workbook()
    _sheet(wb, "Summary", ["Employee ID", "Name", "Department", "Manager", "Working days", "WFO days", "WFH days",
                           "Leave days", "WFO %", "Required WFO days", "Shortfall days", "Compliance",
                           "WFO status"],
           summary_rows, first=True)
    daily = _sheet(wb, "Daily", ["Employee ID", "Name", "Date", "Work mode", "Source", "WFO day", "Office hours",
                                 "WFH hours", "Conflicts"], daily_rows)
    for cell in daily["C"][1:]:
        cell.number_format = "yyyy-mm-dd"
    _sheet(wb, "Policy", ["Setting", "Value"], [
        ["Month", label], ["Minimum WFO days per week", policy.min_days_per_week],
        ["Minimum WFO days per month", policy.min_days_per_month],
        ["Minimum office hours for a WFO day", policy.min_office_minutes / 60],
        ["Counted up to", min(end, today - date.resolution) if start < today else "nothing yet"],
    ])
    buf = io.BytesIO()
    wb.save(buf)
    return f"wfo_report_{label}.xlsx", buf.getvalue()
