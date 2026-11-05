import csv
import io
from datetime import UTC, datetime, time

from sqlalchemy import func, select

from app.models import AttendanceSession, AuditLog, LeaveRecord, LeaveType, RequestStatus, SessionSource, \
    SessionStatus, User
from app.services.reports import safe_cell
from app.tests.conftest import login

RANGE = {"start": "2026-03-09", "end": "2026-03-10"}


def add_session(db, user, start: datetime, end: datetime | None, status=SessionStatus.COMPLETED):
    db.add(AttendanceSession(employee_id=user.id, check_in_at=start, check_out_at=end,
                             net_minutes=int((end - start).total_seconds() // 60) if end else None,
                             status=status, source=SessionSource.SEED))
    db.commit()


def test_overview_snapshot_categories_are_exclusive(client, frozen, org, db):
    # today = Tue 10 Mar 12:00 IST
    add_session(db, org.rahul, datetime(2026, 3, 10, 4, 0, tzinfo=UTC), None, SessionStatus.OPEN)   # present
    add_session(db, org.sneha, datetime(2026, 3, 10, 3, 0, tzinfo=UTC), datetime(2026, 3, 10, 5, 0, tzinfo=UTC))
    db.add(LeaveRecord(employee_id=org.arjun.id, start_date=datetime(2026, 3, 10).date(),
                       end_date=datetime(2026, 3, 10).date(), leave_type=LeaveType.SICK, status=RequestStatus.APPROVED))
    db.commit()
    login(client, org.admin)
    o = client.get("/api/dashboard/overview", params=RANGE).json()
    snap = o["snapshot"]
    assert o["headcount"] == 7
    assert snap["present"] == 2 and snap["on_leave"] == 1 and snap["not_checked_in"] == 4
    assert snap["checked_in_now"] == 1
    assert sum(snap[k] for k in ("present", "absent", "not_checked_in", "on_leave", "incomplete", "off")) == 7


def test_untracked_accounts_excluded_from_stats(client, org, db):
    org.admin.attendance_tracked = False
    db.merge(org.admin)
    db.commit()
    login(client, org.admin)
    assert client.get("/api/dashboard/overview", params=RANGE).json()["headcount"] == 6


def test_employee_rows_search_filter_sort(client, org, db):
    add_session(db, org.rahul, datetime(2026, 3, 9, 4, 0, tzinfo=UTC), datetime(2026, 3, 9, 12, 0, tzinfo=UTC))
    login(client, org.priya)
    rows = client.get("/api/dashboard/employees", params={**RANGE, "search": "sne"}).json()["items"]
    assert [r["full_name"] for r in rows] == ["Sneha Rao"]
    rows = client.get("/api/dashboard/employees", params={**RANGE, "sort": "net_minutes", "order": "desc"}).json()
    assert rows["items"][0]["full_name"] == "Rahul Reddy" and rows["items"][0]["period"]["net_minutes"] == 480
    rows = client.get("/api/dashboard/employees",
                      params={**RANGE, "status": "NOT_YET_CHECKED_IN"}).json()["items"]
    assert len(rows) == 2


def test_trends_and_exceptions(client, org, db):
    add_session(db, org.rahul, datetime(2026, 3, 9, 5, 0, tzinfo=UTC), None, SessionStatus.MISSING_CHECKOUT)
    login(client, org.priya)
    t = client.get("/api/dashboard/trends", params={"start": "2026-03-02", "end": "2026-03-10",
                                                     "granularity": "week"}).json()
    assert len(t["items"]) == 2 and t["headcount"] == 2
    e = client.get("/api/dashboard/exceptions", params=RANGE).json()
    assert e["counts"]["MISSING_CHECKOUT"] == 1
    assert client.get("/api/dashboard/exceptions", params={**RANGE, "exception_type": "MISSING_CHECKOUT"}
                      ).json()["total"] == 1


def test_department_and_manager_comparison(client, org):
    login(client, org.admin)
    depts = client.get("/api/dashboard/departments", params=RANGE).json()
    ai = next(d for d in depts if d["department_name"] == "AI Engineering")
    assert ai["headcount"] == 3 and ai["head_name"] is None
    managers = client.get("/api/dashboard/managers", params=RANGE).json()
    by_name = {m["manager_name"]: m for m in managers}
    assert by_name["Priya Sharma"]["team_size"] == 2
    assert by_name["Vikram Desai"]["team_size"] == 5 and by_name["Vikram Desai"]["direct_reports"] == 2


def test_csv_export_is_scoped_audited_and_injection_safe(client, org, db):
    org.rahul.full_name = "=HYPERLINK(\"http://evil\")"
    db.merge(org.rahul)
    db.commit()
    login(client, org.priya)
    r = client.get("/api/reports/team-attendance.csv", params=RANGE)
    assert r.status_code == 200 and "attachment" in r.headers["content-disposition"]
    rows = list(csv.reader(io.StringIO(r.text.lstrip("﻿"))))
    names = [row[1] for row in rows[1:]]
    assert len(names) == 2 and all(not n.startswith("=") for n in names)
    assert any(n.startswith("'=") for n in names)
    assert db.scalar(select(func.count()).select_from(AuditLog).where(AuditLog.action == "REPORT_EXPORTED")) == 1


def test_all_report_types(client, org):
    login(client, org.admin)
    for path, params in (("monthly-attendance.csv", {"year": 2026, "month": 3}),
                         ("department-attendance.csv", RANGE), ("exceptions.csv", RANGE),
                         ("team-attendance.csv", RANGE),
                         ("employee-attendance.csv", {**RANGE, "employee_id": org.rahul.id})):
        r = client.get(f"/api/reports/{path}", params=params)
        assert r.status_code == 200, path
        assert r.text.count("\n") >= 1


def test_safe_cell():
    assert safe_cell("=1+1") == "'=1+1"
    assert safe_cell("+SUM(A1)") == "'+SUM(A1)"
    assert safe_cell("@cmd") == "'@cmd"
    assert safe_cell("-2") == "'-2"
    assert safe_cell(-2) == "-2"
    assert safe_cell("Normal") == "Normal"
    assert safe_cell(None) == ""


def test_seed_is_repeatable(frozen, db, monkeypatch):
    from app import seed
    monkeypatch.setenv("SEED_ADMIN_EMAIL", "admin@nexa.example")
    monkeypatch.setenv("SEED_ADMIN_PASSWORD", "AdminPassw0rd!")
    monkeypatch.setenv("SEED_DEMO_PASSWORD", "DemoPassw0rd!")
    frozen.set_local(datetime(2026, 3, 10).date(), time(12, 0))
    first = seed.run(db)
    users_after_first = db.scalar(select(func.count()).select_from(User))
    sessions_after_first = db.scalar(select(func.count()).select_from(AttendanceSession))
    assert first["sessions_created"] > 300
    seed.run(db)
    assert db.scalar(select(func.count()).select_from(User)) == users_after_first == 13
    assert db.scalar(select(func.count()).select_from(AttendanceSession)) == sessions_after_first
    assert db.scalar(select(func.count()).select_from(AttendanceSession).where(
        AttendanceSession.status == SessionStatus.MISSING_CHECKOUT)) > 0


def test_seed_requires_passwords(db, monkeypatch):
    import pytest

    from app import seed
    monkeypatch.delenv("SEED_ADMIN_PASSWORD", raising=False)
    with pytest.raises(seed.SeedError):
        seed.run(db)


def test_employee_trends_endpoint(client, org, db):
    add_session(db, org.rahul, datetime(2026, 3, 9, 4, 0, tzinfo=UTC), datetime(2026, 3, 9, 12, 0, tzinfo=UTC))
    login(client, org.rahul)
    t = client.get(f"/api/attendance/employees/{org.rahul.id}/trends",
                   params={"start": "2026-02-01", "end": "2026-03-10", "granularity": "month"}).json()
    assert [b["label"] for b in t["items"]] == ["Feb 2026", "Mar 2026"]
    assert t["items"][1]["net_minutes"] == 480 and t["items"][1]["present"] == 1
    assert client.get(f"/api/attendance/employees/{org.sneha.id}/trends").status_code == 404
