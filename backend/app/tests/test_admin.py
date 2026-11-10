from sqlalchemy import select

from app.models import AuditLog, Role
from app.tests.conftest import login


def new_user(**kw):
    body = {"employee_code": "EMP900", "full_name": "Test Person", "email": "test.person@test.example",
            "password": "Welcome12345", "role": "EMPLOYEE"}
    body.update(kw)
    return body


def test_create_employee_and_audit(client, org, db):
    h = login(client, org.admin)
    r = client.post("/api/admin/users", headers=h,
                    json=new_user(department_id=org.ai.id, manager_id=org.priya.id))
    assert r.status_code == 201, r.text
    body = r.json()
    assert body["manager"]["full_name"] == "Priya Sharma" and "password" not in str(body)
    log = db.scalar(select(AuditLog).where(AuditLog.action == "USER_CREATED"))
    assert log.actor_user_id == org.admin.id and "Welcome12345" not in str(log.after_values)
    assert "password_hash" not in log.after_values


def test_duplicate_email_and_code_rejected(client, org):
    h = login(client, org.admin)
    assert client.post("/api/admin/users", headers=h, json=new_user()).status_code == 201
    assert client.post("/api/admin/users", headers=h, json=new_user(employee_code="EMP901")).status_code == 409
    assert client.post("/api/admin/users", headers=h,
                       json=new_user(email="other@test.example", employee_code="emp900")).status_code == 409


def test_employee_codes_may_have_inner_spaces_and_dots(client, org):
    # Attendance files use IDs like "Intern - 10001" or "Guest 2".
    h = login(client, org.admin)
    r = client.post("/api/admin/users", headers=h, json=new_user(employee_code="Intern - 10001"))
    assert r.status_code == 201 and r.json()["employee_code"] == "INTERN - 10001"
    for bad in (" EMP1", "EMP1 ", "EMP#1", "-EMP"):
        r = client.post("/api/admin/users", headers=h, json=new_user(employee_code=bad, email="x@test.example"))
        assert r.status_code == 422, bad


def test_manager_assignment_validation(client, org):
    h = login(client, org.admin)
    r = client.patch(f"/api/admin/users/{org.priya.id}", headers=h, json={"manager_id": org.priya.id})
    assert r.status_code == 422  # own manager
    r = client.patch(f"/api/admin/users/{org.vikram.id}", headers=h, json={"manager_id": org.priya.id})
    assert r.status_code == 422 and "circular" in r.json()["error"]["message"]
    r = client.patch(f"/api/admin/users/{org.sneha.id}", headers=h, json={"manager_id": org.rahul.id})
    assert r.status_code == 422  # employees cannot manage


def test_reassign_manager_and_department(client, org, db):
    h = login(client, org.admin)
    r = client.patch(f"/api/admin/users/{org.arjun.id}", headers=h,
                     json={"manager_id": org.priya.id, "department_id": org.ai.id})
    assert r.status_code == 200
    assert r.json()["manager"]["id"] == org.priya.id and r.json()["department"]["id"] == org.ai.id
    log = db.scalar(select(AuditLog).where(AuditLog.entity_id == str(org.arjun.id)))
    assert log.before_values["manager_id"] == org.anil.id and log.after_values["manager_id"] == org.priya.id


def test_cannot_demote_or_deactivate_last_admin(client, org):
    h = login(client, org.admin)
    r = client.patch(f"/api/admin/users/{org.admin.id}", headers=h, json={"role": "EMPLOYEE"})
    assert r.status_code == 422
    r = client.patch(f"/api/admin/users/{org.admin.id}", headers=h, json={"is_active": False})
    assert r.status_code == 422


def test_last_admin_rule_with_second_admin(client, org, factory):
    other = factory.user("Second Admin", Role.ADMIN)
    h = login(client, org.admin)
    assert client.patch(f"/api/admin/users/{other.id}", headers=h, json={"role": "MANAGER"}).status_code == 200
    third = factory.user("Third Admin", Role.ADMIN)
    h = login(client, third)
    r = client.patch(f"/api/admin/users/{org.admin.id}", headers=h, json={"role": "MANAGER"})
    assert r.status_code == 200
    r = client.patch(f"/api/admin/users/{third.id}", headers=h, json={"role": "MANAGER"})
    assert r.status_code == 422  # own role / last admin


def test_cannot_demote_manager_with_reports_or_deactivate(client, org):
    h = login(client, org.admin)
    assert client.patch(f"/api/admin/users/{org.priya.id}", headers=h, json={"role": "EMPLOYEE"}).status_code == 422
    assert client.patch(f"/api/admin/users/{org.priya.id}", headers=h, json={"is_active": False}).status_code == 422


def test_deactivation_revokes_sessions(client, org):
    from fastapi.testclient import TestClient

    from app.main import app
    other = TestClient(app)
    login(other, org.rahul)
    h = login(client, org.admin)
    assert client.patch(f"/api/admin/users/{org.rahul.id}", headers=h, json={"is_active": False}).status_code == 200
    assert other.get("/api/auth/me").status_code == 401


def test_employee_cannot_escalate_privileges(client, org):
    h = login(client, org.rahul)
    r = client.patch(f"/api/admin/users/{org.rahul.id}", headers=h, json={"role": "ADMIN"})
    assert r.status_code == 403
    h = login(client, org.priya)
    r = client.patch(f"/api/admin/users/{org.rahul.id}", headers=h, json={"role": "ADMIN"})
    assert r.status_code == 403


def test_department_crud_and_unique_names(client, org):
    h = login(client, org.admin)
    r = client.post("/api/admin/departments", headers=h, json={"name": "Finance", "department_head_id": org.anil.id})
    assert r.status_code == 201 and r.json()["head"]["id"] == org.anil.id
    assert client.post("/api/admin/departments", headers=h, json={"name": "finance"}).status_code == 409
    did = r.json()["id"]
    assert client.patch(f"/api/admin/departments/{did}", headers=h, json={"description": "Money"}).status_code == 200
    assert client.delete(f"/api/admin/departments/{org.ai.id}", headers=h).status_code == 409  # has members
    assert client.delete(f"/api/admin/departments/{did}", headers=h).status_code == 200


def test_schedule_validation_and_overlap(client, org):
    h = login(client, org.admin)
    body = {"name": "Late shift", "employee_id": org.rahul.id, "start_time": "12:00", "end_time": "20:00",
            "required_minutes": 420, "working_days": [1, 2, 3, 4], "effective_from": "2026-03-01"}
    assert client.post("/api/admin/schedules", headers=h, json=body).status_code == 201
    assert client.post("/api/admin/schedules", headers=h, json={**body, "effective_from": "2026-04-01"}
                       ).status_code == 409
    assert client.post("/api/admin/schedules", headers=h, json={**body, "employee_id": org.sneha.id,
                                                                "working_days": [8]}).status_code == 422
    # schedule change is reflected in calculations: Friday is no longer a working day for Rahul
    s = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                   params={"start": "2026-03-06", "end": "2026-03-06"}).json()
    assert s["weekend_days"] == 1 and s["expected_days"] == 0


def test_holiday_management(client, org):
    h = login(client, org.admin)
    r = client.post("/api/admin/holidays", headers=h, json={"name": "Festival", "holiday_date": "2026-03-04"})
    assert r.status_code == 201
    assert client.post("/api/admin/holidays", headers=h,
                       json={"name": "Dup", "holiday_date": "2026-03-04"}).status_code == 409
    s = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                   params={"start": "2026-03-04", "end": "2026-03-04"}).json()
    assert s["holiday_days"] == 1 and s["absent_days"] == 0


def test_audit_log_listing_and_filter(client, org):
    h = login(client, org.admin)
    client.post("/api/admin/departments", headers=h, json={"name": "Legal"})
    logs = client.get("/api/admin/audit-logs", params={"action": "DEPARTMENT_CREATED"}).json()
    assert logs["total"] == 1 and logs["items"][0]["actor_name"] == "Admin User"
    assert client.get("/api/admin/audit-logs", params={"search": "admin user"}).json()["total"] >= 2


def test_org_hierarchy(client, org):
    login(client, org.admin)
    data = client.get("/api/org/hierarchy").json()
    ai = next(d for d in data["departments"] if d["name"] == "AI Engineering")
    priya = ai["nodes"][0]
    assert priya["full_name"] == "Priya Sharma"
    assert sorted(c["full_name"] for c in priya["children"]) == ["Rahul Reddy", "Sneha Rao"]
