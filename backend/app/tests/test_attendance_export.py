"""One employee's attendance as Excel and as a branded PDF (same access rule as the CSV export)."""

import io
from datetime import date, time

import pytest
from openpyxl import load_workbook
from pdfminer.high_level import extract_text
from sqlalchemy import func, select

from app.core.config import get_settings
from app.models import AuditLog
from app.tests.conftest import login

PERIOD = {"start": "2026-02-01", "end": "2026-03-31"}
FEB_DAYS = [2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 16, 17]   # 12 office days: on target
MAR_DAYS = [2, 3, 4, 5, 6, 9, 10, 11, 12]               # 9 office days: 3 short, "Close"


@pytest.fixture
def two_months(org, factory, frozen):
    frozen.set_local(date(2026, 4, 15), time(12, 0))
    for d in FEB_DAYS:
        factory.timesheet(org.rahul, date(2026, 2, d))
    for d in MAR_DAYS:
        factory.timesheet(org.rahul, date(2026, 3, d), minutes=450)
    factory.cover(date(2026, 2, 1), date(2026, 3, 31), [org.rahul])
    return org


def test_excel_export_has_summary_months_and_days(client, two_months, db):
    org = two_months
    login(client, org.rahul)
    r = client.get("/api/reports/employee-attendance.xlsx", params={**PERIOD, "employee_id": org.rahul.id})
    assert r.status_code == 200, r.text
    assert r.headers["content-type"].startswith("application/vnd.openxmlformats")
    assert f"attendance_{org.rahul.employee_code}_2026-02-01_2026-03-31.xlsx" in r.headers["content-disposition"]
    wb = load_workbook(io.BytesIO(r.content))
    assert wb.sheetnames == ["Summary", "By month", "Daily"]

    summary = {row[0]: row[1:] for row in wb["Summary"].iter_rows(values_only=True) if row[0]}
    assert summary["Employee"][0] == "Rahul Reddy" and summary["Period"][0] == "01 Feb 2026 – 31 Mar 2026"
    # Two months: the requirement is 12 for each, so 21 office days of 24 is 3 short.
    assert summary["Office (WFO) days"][:2] == (21, "of 24 required · Close · on target in 1 of 2 months")
    assert summary["Average office hours"][0] == "7h 30m"

    months = list(wb["By month"].iter_rows(values_only=True))
    assert months[0][:5] == ("Month", "Working days", "Office days", "Required", "WFO status")
    assert months[1][:5] == ("Feb 2026", 20, 12, 12, "On target")
    assert months[2][:5] == ("Mar 2026", 22, 9, 12, "Close")
    assert months[1][10] is None and months[2][10] == 7.5   # hours only where the timesheet has them

    days = list(wb["Daily"].iter_rows(values_only=True))
    assert len(days) == 1 + 59
    by_date = {d[0].date(): d for d in days[1:]}
    assert by_date[date(2026, 2, 2)][2] == "In office" and by_date[date(2026, 2, 18)][2] == "Absent"
    assert by_date[date(2026, 2, 7)][2] == "Weekly off" and by_date[date(2026, 3, 2)][5] == 7.5
    assert db.scalar(select(func.count()).select_from(AuditLog).where(AuditLog.action == "REPORT_EXPORTED")) == 1


def test_pdf_export_carries_the_brand_and_the_figures(client, two_months, monkeypatch):
    org = two_months
    monkeypatch.setattr(get_settings(), "report_company_name", "Acme Corp")
    monkeypatch.setattr(get_settings(), "report_brand_color", "#1F6F5A")
    monkeypatch.setattr(get_settings(), "report_logo_file", "data/does-not-exist.png")  # falls back to the name
    login(client, org.priya)  # Rahul's manager
    r = client.get("/api/reports/employee-attendance.pdf", params={**PERIOD, "employee_id": org.rahul.id})
    assert r.status_code == 200, r.text
    assert r.headers["content-type"] == "application/pdf" and r.content.startswith(b"%PDF")
    assert r.headers["content-disposition"].endswith('.pdf"')
    text = " ".join(extract_text(io.BytesIO(r.content)).split())
    for expected in ("Acme Corp", "Attendance report", "Rahul Reddy", "01 Feb 2026 – 31 Mar 2026", "21 of 24",
                     "Office days by month", "Feb 2026", "Mar 2026", "On target", "Close", "Daily attendance",
                     "Page 1 of"):
        assert expected in text, expected


def test_exports_follow_the_same_access_rule_as_the_dashboards(client, two_months):
    org = two_months
    login(client, org.rahul)
    for ext in ("xlsx", "pdf"):
        own = client.get(f"/api/reports/employee-attendance.{ext}", params={**PERIOD, "employee_id": org.rahul.id})
        other = client.get(f"/api/reports/employee-attendance.{ext}", params={**PERIOD, "employee_id": org.sneha.id})
        assert own.status_code == 200 and other.status_code == 404
    login(client, org.anil)  # a manager, but not of Rahul
    assert client.get("/api/reports/employee-attendance.pdf",
                      params={**PERIOD, "employee_id": org.rahul.id}).status_code == 404


def test_excel_export_neutralises_formulas_and_awkward_ids(client, two_months, db):
    org = two_months
    org.rahul.full_name = '=HYPERLINK("http://evil")'
    org.rahul.employee_code = "Intern - 22026"
    db.merge(org.rahul)
    db.commit()
    login(client, org.admin)
    r = client.get("/api/reports/employee-attendance.xlsx", params={**PERIOD, "employee_id": org.rahul.id})
    assert 'filename="attendance_Intern_-_22026_2026-02-01_2026-03-31.xlsx"' in r.headers["content-disposition"]
    name = next(row[1] for row in load_workbook(io.BytesIO(r.content))["Summary"].iter_rows(values_only=True)
                if row[0] == "Employee")
    assert name.startswith("'=")


def test_months_endpoint_feeds_the_dashboard_chart(client, two_months):
    org = two_months
    login(client, org.rahul)
    r = client.get(f"/api/attendance/employees/{org.rahul.id}/months", params={"start": "2026-01-01", "end": "2026-04-15"})
    assert r.status_code == 200, r.text
    months = r.json()
    assert [(m["label"], m["covered"], m["office_days"], m["required_days"], m["wfo_band"]) for m in months] == [
        ("Jan 2026", False, 0, 12, "BELOW"), ("Feb 2026", True, 12, 12, "MET"), ("Mar 2026", True, 9, 12, "NEAR"),
        ("Apr 2026", False, 0, 5, "BELOW")]
    assert months[3]["partial"] and months[2]["average_net_minutes"] == 450
    assert client.get(f"/api/attendance/employees/{org.sneha.id}/months",
                      params={"start": "2026-01-01", "end": "2026-04-15"}).status_code == 404


def test_months_without_a_timesheet_ask_for_nothing_in_the_report(client, two_months):
    org = two_months
    login(client, org.rahul)
    r = client.get("/api/reports/employee-attendance.xlsx",
                   params={"start": "2026-01-01", "end": "2026-03-31", "employee_id": org.rahul.id})
    wb = load_workbook(io.BytesIO(r.content))
    summary = {row[0]: row[1:] for row in wb["Summary"].iter_rows(values_only=True) if row[0]}
    assert summary["Office (WFO) days"][:2] == (21, "of 24 required · Close · on target in 1 of 2 months")
    assert list(wb["By month"].iter_rows(values_only=True))[1][:5] == ("Jan 2026", 22, 0, 12, "No timesheet")
