"""Manager reporting scope on PostgreSQL (recursive CTE + API enforcement)."""

from sqlalchemy import text

from app.models import Role
from app.services.scope import subtree_ids
from app.tests.conftest import login

RANGE = {"start": "2026-03-01", "end": "2026-03-10"}


def test_recursive_subtree_on_postgres(org, factory, db):
    lead = factory.user("Team Lead", Role.MANAGER, manager=org.priya)
    junior = factory.user("Junior Dev", manager=lead)
    assert subtree_ids(db, org.priya.id) == {org.rahul.id, org.sneha.id, lead.id, junior.id}
    assert subtree_ids(db, org.vikram.id) == {org.priya.id, org.anil.id, org.rahul.id, org.sneha.id, org.arjun.id,
                                              lead.id, junior.id}
    assert subtree_ids(db, org.rahul.id) == set()


def test_subtree_query_terminates_on_a_cycle(org, db):
    # The API rejects cycles; force one in SQL to prove the recursive CTE (UNION) still terminates.
    db.execute(text("SET LOCAL statement_timeout = '5s'"))
    db.execute(text("UPDATE users SET manager_id = :arjun WHERE id = :vikram"),
               {"arjun": org.arjun.id, "vikram": org.vikram.id})
    ids = subtree_ids(db, org.vikram.id)
    assert {org.anil.id, org.arjun.id, org.priya.id}.issubset(ids)
    db.rollback()


def test_manager_scope_enforced_by_api_on_postgres(client, org):
    login(client, org.priya)
    rows = client.get("/api/dashboard/employees", params={**RANGE, "page_size": 50}).json()["items"]
    assert sorted(r["employee_id"] for r in rows) == sorted([org.rahul.id, org.sneha.id])
    assert client.get(f"/api/attendance/employees/{org.arjun.id}/summary", params=RANGE).status_code == 404
    assert client.get("/api/dashboard/employees", params={**RANGE, "manager_id": org.anil.id}).status_code == 404
    csv = client.get("/api/reports/team-attendance.csv", params=RANGE).text
    assert "Rahul Reddy" in csv and "Arjun Kumar" not in csv

    login(client, org.vikram)
    rows = client.get("/api/dashboard/employees", params={**RANGE, "page_size": 50}).json()["items"]
    assert len(rows) == 5

    login(client, org.rahul)
    assert client.get("/api/dashboard/overview", params=RANGE).status_code == 403


def test_review_outside_scope_is_rejected_on_postgres(client, org):
    h = login(client, org.arjun)
    cid = client.post("/api/corrections", headers=h, json={
        "attendance_date": "2026-03-09", "check_in_time": "09:30", "check_out_time": "18:00",
        "break_minutes": 60, "reason": "Badge reader outage"}).json()["id"]
    h = login(client, org.priya)
    assert client.post(f"/api/corrections/{cid}/approve", headers=h, json={}).status_code == 404
    h = login(client, org.anil)
    assert client.post(f"/api/corrections/{cid}/approve", headers=h, json={}).status_code == 200
