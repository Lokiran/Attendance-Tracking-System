"""Role and reporting-scope enforcement on the API."""

import pytest

from app.tests.conftest import login

RANGE = {"start": "2026-03-01", "end": "2026-03-10"}


def ids(rows):
    return sorted(r["employee_id"] for r in rows)


def test_employee_can_read_only_own_attendance(client, org):
    login(client, org.rahul)
    assert client.get(f"/api/attendance/employees/{org.rahul.id}/summary", params=RANGE).status_code == 200
    for other in (org.sneha, org.priya, org.arjun):
        r = client.get(f"/api/attendance/employees/{other.id}/summary", params=RANGE)
        assert r.status_code == 404  # indistinguishable from a missing employee


@pytest.mark.parametrize("path", ["/api/dashboard/overview", "/api/dashboard/employees", "/api/dashboard/trends",
                                  "/api/team/members", "/api/reports/team-attendance.csv"])
def test_employee_cannot_use_team_endpoints(client, org, path):
    login(client, org.rahul)
    assert client.get(path, params=RANGE).status_code == 403


@pytest.mark.parametrize("path", ["/api/admin/users", "/api/admin/audit-logs", "/api/dashboard/departments",
                                  "/api/org/hierarchy", "/api/admin/schedules"])
def test_non_admins_cannot_use_admin_endpoints(client, org, path):
    login(client, org.priya)
    assert client.get(path, params=RANGE).status_code == 403


def test_manager_sees_only_reporting_subtree(client, org):
    login(client, org.priya)
    rows = client.get("/api/dashboard/employees", params=RANGE).json()["items"]
    assert ids(rows) == sorted([org.rahul.id, org.sneha.id])
    assert client.get(f"/api/attendance/employees/{org.rahul.id}/summary", params=RANGE).status_code == 200
    assert client.get(f"/api/attendance/employees/{org.arjun.id}/summary", params=RANGE).status_code == 404
    assert client.get(f"/api/attendance/employees/{org.anil.id}/summary", params=RANGE).status_code == 404


def test_nested_manager_sees_whole_subtree(client, org):
    login(client, org.vikram)
    rows = client.get("/api/dashboard/employees", params={**RANGE, "page_size": 50}).json()["items"]
    assert ids(rows) == sorted([org.priya.id, org.anil.id, org.rahul.id, org.sneha.id, org.arjun.id])
    team = client.get("/api/dashboard/employees", params={**RANGE, "manager_id": org.priya.id}).json()["items"]
    assert ids(team) == sorted([org.rahul.id, org.sneha.id])


def test_manager_cannot_query_unrelated_manager_scope(client, org):
    login(client, org.priya)
    r = client.get("/api/dashboard/employees", params={**RANGE, "manager_id": org.anil.id})
    assert r.status_code == 404
    r = client.get("/api/reports/team-attendance.csv", params={**RANGE, "manager_id": org.anil.id})
    assert r.status_code == 404


def test_reassignment_changes_access_immediately(client, org, db):
    login(client, org.priya)
    assert client.get(f"/api/attendance/employees/{org.arjun.id}/summary", params=RANGE).status_code == 404
    org.arjun.manager_id = org.priya.id
    db.merge(org.arjun)
    db.commit()
    assert client.get(f"/api/attendance/employees/{org.arjun.id}/summary", params=RANGE).status_code == 200


def test_admin_sees_everyone(client, org):
    login(client, org.admin)
    rows = client.get("/api/dashboard/employees", params={**RANGE, "page_size": 50}).json()["items"]
    assert len(rows) == 7
    r = client.get("/api/dashboard/employees", params={**RANGE, "department_id": org.ai.id}).json()["items"]
    assert ids(r) == sorted([org.priya.id, org.rahul.id, org.sneha.id])


def test_employee_export_restricted_to_self(client, org):
    login(client, org.rahul)
    ok = client.get("/api/reports/employee-attendance.csv", params={**RANGE, "employee_id": org.rahul.id})
    assert ok.status_code == 200 and ok.headers["content-type"].startswith("text/csv")
    denied = client.get("/api/reports/employee-attendance.csv", params={**RANGE, "employee_id": org.sneha.id})
    assert denied.status_code == 404


def test_employee_cannot_list_others_requests(client, org):
    login(client, org.rahul)
    assert client.get("/api/corrections", params={"scope": "review"}).status_code == 403
    assert client.get("/api/leaves", params={"scope": "review"}).status_code == 403
