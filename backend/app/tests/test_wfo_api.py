"""WFO API: classification through real endpoints, role boundaries, overrides, policy, summary and export.

Clock: Tuesday 10 March 2026, 12:00 IST (conftest). org: Vikram -> Priya -> Rahul, Sneha; Vikram -> Anil -> Arjun.
"""

import io
from datetime import UTC, date, datetime, timedelta

import openpyxl
import pytest
from sqlalchemy import select

from app.models import AttendanceDaily, AuditLog, DailyStatus, WorkMode
from app.tests.conftest import login

MONTH = {"month": "2026-03"}


def ist(d: date, h: int, m: int = 0) -> datetime:
    return datetime(d.year, d.month, d.day, h, m, tzinfo=UTC) - timedelta(hours=5, minutes=30)


def worked(db, user, d: date, hours: float = 8.5, location: WorkMode | None = None):
    """An imported timesheet day: hours, and the work mode column (None: the file had no mode for it)."""
    start = ist(d, 9, 30)
    db.add(AttendanceDaily(employee_id=user.id, work_date=d, office_minutes=0, wfh_minutes=0, corrected=False,
                           conflicts=[], status=DailyStatus.ABSENT, upload_work_mode=location,
                           upload_minutes=int(hours * 60), first_in=start,
                           last_out=start + timedelta(minutes=int(hours * 60))))
    db.commit()


def day(body: dict, d: str) -> dict:
    return next(x for x in body["days"] if x["date"] == d)


# ------------------------------------------------------------------ classification through the API

def test_me_wfo_classifies_timesheet_days(client, org, db):
    worked(db, org.rahul, date(2026, 3, 9), location=WorkMode.OFFICE)
    worked(db, org.rahul, date(2026, 3, 6), location=WorkMode.WFH)
    worked(db, org.rahul, date(2026, 3, 5))  # no work mode: hours without an office signal
    worked(db, org.rahul, date(2026, 3, 4), hours=3, location=WorkMode.OFFICE)  # below the 4 h minimum
    login(client, org.rahul)
    body = client.get("/api/me/wfo", params=MONTH).json()
    assert (day(body, "2026-03-09")["work_mode"], day(body, "2026-03-09")["wfo_source"]) == ("OFFICE", "UPLOAD_COLUMN")
    assert day(body, "2026-03-09")["is_wfo_day"] is True
    assert (day(body, "2026-03-06")["work_mode"], day(body, "2026-03-06")["wfo_source"]) == ("WFH", "UPLOAD_COLUMN")
    assert (day(body, "2026-03-05")["work_mode"], day(body, "2026-03-05")["wfo_source"]) == ("WFH", "INFERRED")
    assert day(body, "2026-03-04")["work_mode"] == "OFFICE" and day(body, "2026-03-04")["is_wfo_day"] is False
    assert day(body, "2026-03-07")["work_mode"] == "WEEKOFF"
    assert day(body, "2026-03-10")["in_progress"] and day(body, "2026-03-11")["future"]
    s = body["summary"]
    assert s["wfo_days"] == 1 and s["wfh_days"] == 2 and s["office_days_below_minimum"] == 1
    assert s["working_days"] == 6  # 2-6 and 9 March; today (the 10th) is not counted yet
    assert body["policy"] == {"min_days_per_week": 3, "min_days_per_month": 12, "min_office_minutes": 240}
    # the week of Sun 1 March is judged on 23 Feb - 1 Mar (no attendance in February): short, like 2-8 March
    assert [w["status"] for w in body["weekly_wfo_breakdown"]][:2] == ["SHORT", "SHORT"]
    assert body["weekly_wfo_breakdown"][0]["week_start"] == "2026-02-23"


# ------------------------------------------------------------------ role boundaries

def test_wfo_role_boundaries(client, org):
    assert client.get("/api/me/wfo").status_code == 401
    login(client, org.rahul)
    assert client.get("/api/me/wfo", params=MONTH).status_code == 200
    assert client.get("/api/team/wfo", params=MONTH).status_code == 403
    assert client.get("/api/org/wfo", params=MONTH).status_code == 403
    assert client.get(f"/api/team/wfo/employees/{org.sneha.id}", params=MONTH).status_code == 403

    login(client, org.priya)
    names = {r["employee"]["full_name"] for r in client.get("/api/team/wfo", params=MONTH).json()["rows"]}
    assert names == {"Priya Sharma", "Rahul Reddy", "Sneha Rao"}  # own + reporting line, never Arjun
    assert client.get(f"/api/team/wfo/employees/{org.rahul.id}", params=MONTH).status_code == 200
    assert client.get(f"/api/team/wfo/employees/{org.arjun.id}", params=MONTH).status_code == 403
    assert client.get("/api/team/wfo", params={**MONTH, "employee_id": org.arjun.id}).status_code == 403
    assert client.get("/api/team/wfo/employees/999999", params=MONTH).status_code == 403
    assert client.get("/api/org/wfo", params=MONTH).status_code == 403
    assert client.get("/api/org/wfo/export.xlsx", params=MONTH).status_code == 403

    login(client, org.vikram)  # whole reporting line, including indirect reports
    names = {r["employee"]["full_name"] for r in client.get("/api/team/wfo", params=MONTH).json()["rows"]}
    assert names == {"Vikram Desai", "Priya Sharma", "Anil Kumar", "Rahul Reddy", "Sneha Rao", "Arjun Kumar"}

    login(client, org.admin)
    assert client.get(f"/api/team/wfo/employees/{org.arjun.id}", params=MONTH).status_code == 200
    assert client.get("/api/me/wfo", params={"month": "2026-13"}).status_code == 422


def test_org_wfo_groups_by_direct_manager_and_filters(client, org, db):
    worked(db, org.rahul, date(2026, 3, 9), location=WorkMode.OFFICE)
    login(client, org.admin)
    body = client.get("/api/org/wfo", params=MONTH).json()
    groups = {g["manager_name"]: {r["employee"]["full_name"] for r in g["rows"]} for g in body["groups"]}
    assert groups["Priya Sharma"] == {"Rahul Reddy", "Sneha Rao"}
    assert groups["Anil Kumar"] == {"Arjun Kumar"}
    assert groups["Vikram Desai"] == {"Priya Sharma", "Anil Kumar"}
    assert body["totals"]["headcount"] == sum(len(v) for v in groups.values())
    assert body["totals"]["wfo_days"] == 1
    assert body["totals"]["compliant"] + body["totals"]["non_compliant"] == body["totals"]["headcount"]
    only_priya = client.get("/api/org/wfo", params={**MONTH, "manager_id": org.priya.id}).json()
    assert {r["employee"]["full_name"] for g in only_priya["groups"] for r in g["rows"]} == {"Rahul Reddy", "Sneha Rao"}
    by_dept = client.get("/api/org/wfo", params={**MONTH, "department_id": org.se.id}).json()
    assert {r["employee"]["full_name"] for g in by_dept["groups"] for r in g["rows"]} == {"Anil Kumar", "Arjun Kumar"}


# ------------------------------------------------------------------ manual override

def test_admin_override_wins_and_is_audited(client, org, db):
    worked(db, org.rahul, date(2026, 3, 5))  # inferred WFH
    h = login(client, org.priya)
    url = f"/api/admin/attendance/{org.rahul.id}/2026-03-05/work-mode"
    assert client.put(url, headers=h, json={"work_mode": "OFFICE"}).status_code == 403
    h = login(client, org.admin)
    r = client.put(url, headers=h, json={"work_mode": "OFFICE", "note": "Client visit, badge missing"})
    assert r.status_code == 200 and r.json()["wfo_source"] == "MANUAL_OVERRIDE" and r.json()["overridden"]
    d = day(client.get(f"/api/team/wfo/employees/{org.rahul.id}", params=MONTH).json(), "2026-03-05")
    assert (d["work_mode"], d["wfo_source"], d["overridden"]) == ("OFFICE", "MANUAL_OVERRIDE", True)
    assert d["override_note"] == "Client visit, badge missing" and d["is_wfo_day"]  # 8.5 h from the timesheet
    entry = db.scalar(select(AuditLog).where(AuditLog.action == "WORK_MODE_OVERRIDDEN"))
    assert entry.actor_user_id == org.admin.id and entry.entity_id == f"{org.rahul.id}:2026-03-05"
    assert entry.before_values["work_mode"] == "WFH" and entry.after_values["work_mode"] == "OFFICE"

    assert client.delete(url, headers=h).status_code == 200
    d = day(client.get(f"/api/team/wfo/employees/{org.rahul.id}", params=MONTH).json(), "2026-03-05")
    assert (d["work_mode"], d["wfo_source"]) == ("WFH", "INFERRED")
    assert db.scalar(select(AuditLog).where(AuditLog.action == "WORK_MODE_OVERRIDE_CLEARED")) is not None
    assert client.delete(url, headers=h).status_code == 404
    assert client.put(url, headers=h, json={"work_mode": "PARTY"}).status_code == 422
    assert client.put(f"/api/admin/attendance/{org.rahul.id}/2024-01-01/work-mode", headers=h,
                      json={"work_mode": "LEAVE"}).status_code == 422  # before joining


def test_leave_can_only_come_from_an_override_or_import(client, org, db):
    h = login(client, org.admin)
    client.put(f"/api/admin/attendance/{org.rahul.id}/2026-03-03/work-mode", headers=h, json={"work_mode": "LEAVE"})
    body = client.get(f"/api/team/wfo/employees/{org.rahul.id}", params=MONTH).json()
    assert day(body, "2026-03-03")["work_mode"] == "LEAVE" and body["summary"]["leave_days"] == 1
    assert body["summary"]["working_days"] == 5  # 2-6 and 9 March minus the leave day


# ------------------------------------------------------------------ policy, summary, export

def test_policy_changes_what_counts_as_a_wfo_day(client, org, db):
    worked(db, org.rahul, date(2026, 3, 9), hours=3, location=WorkMode.OFFICE)
    h = login(client, org.priya)
    assert client.get("/api/admin/wfo/policy").status_code == 403
    h = login(client, org.admin)
    assert client.put("/api/admin/wfo/policy", headers=h, json={"min_office_minutes": 2000}).status_code == 422
    r = client.put("/api/admin/wfo/policy", headers=h, json={"min_office_minutes": 120, "min_days_per_week": 2})
    assert r.json() == {"min_days_per_week": 2, "min_days_per_month": 12, "min_office_minutes": 120}
    assert db.scalar(select(AuditLog).where(AuditLog.action == "WFO_POLICY_UPDATED")) is not None
    body = client.get(f"/api/team/wfo/employees/{org.rahul.id}", params=MONTH).json()
    assert day(body, "2026-03-09")["is_wfo_day"] is True  # 3 h now meets the 2 h minimum


def test_existing_summary_includes_wfo(client, org, db):
    worked(db, org.rahul, date(2026, 3, 9), location=WorkMode.OFFICE)
    login(client, org.rahul)
    s = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                   params={"start": "2026-03-01", "end": "2026-03-10"}).json()
    assert s["wfo"]["wfo_days"] == 1 and s["wfo"]["compliance"] in ("COMPLIANT", "NON_COMPLIANT")
    assert "weekly_wfo_breakdown" in s["wfo"] and "wfo_percentage" in s["wfo"]


def test_excel_export(client, org, db):
    org.sneha.full_name = "=HYPERLINK(\"http://evil\")"
    db.commit()
    worked(db, org.rahul, date(2026, 3, 9), location=WorkMode.OFFICE)
    login(client, org.admin)
    r = client.get("/api/org/wfo/export.xlsx", params=MONTH)
    assert r.status_code == 200 and r.headers["content-type"].startswith("application/vnd.openxmlformats")
    assert 'filename="wfo_report_2026-03.xlsx"' in r.headers["content-disposition"]
    wb = openpyxl.load_workbook(io.BytesIO(r.content))
    assert wb.sheetnames == ["Summary", "Daily", "Policy"]
    summary = list(wb["Summary"].iter_rows(values_only=True))
    assert summary[0][:3] == ("Employee ID", "Name", "Department")
    rahul = next(row for row in summary if row[1] == "Rahul Reddy")
    assert rahul[5] == 1  # WFO days
    assert any(str(row[1]).startswith("'=HYPERLINK") for row in summary)  # formula injection neutralised
    daily = list(wb["Daily"].iter_rows(values_only=True))
    assert any(row[1] == "Rahul Reddy" and row[3] == "OFFICE" and row[5] == "Yes" for row in daily[1:])
    assert db.scalar(select(AuditLog).where(AuditLog.action == "REPORT_EXPORTED")) is not None


@pytest.mark.parametrize("month", ["2026-3", "26-03", "2026-03-01"])
def test_month_parameter_is_validated(client, org, month):
    login(client, org.rahul)
    assert client.get("/api/me/wfo", params={"month": month}).status_code == 422
