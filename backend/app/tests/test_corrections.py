from datetime import UTC, date, datetime, time

from sqlalchemy import select

from app.models import AttendanceSession, AuditLog, SessionSource, SessionStatus
from app.tests.conftest import login

MON = date(2026, 3, 9)


def missing_session(db, employee):
    s = AttendanceSession(employee_id=employee.id, check_in_at=datetime(2026, 3, 9, 4, 0, tzinfo=UTC),  # 09:30 IST
                          status=SessionStatus.MISSING_CHECKOUT, source=SessionSource.WEB)
    db.add(s)
    db.commit()
    return s


def submit(client, headers, **overrides):
    body = {"attendance_date": "2026-03-09", "check_in_time": "09:30", "check_out_time": "18:30",
            "break_minutes": 60, "reason": "Forgot to check out at the end of the day"}
    body.update(overrides)
    return client.post("/api/corrections", headers=headers, json=body)


def test_full_approval_flow_voids_original_and_is_audited(client, org, db):
    original = missing_session(db, org.rahul)
    h = login(client, org.rahul)
    r = submit(client, h, session_id=original.id)
    assert r.status_code == 201, r.text
    cid = r.json()["id"]
    assert r.json()["status"] == "PENDING" and r.json()["request_type"] == "MODIFY_SESSION"
    assert r.json()["original_values"]["status"] == "MISSING_CHECKOUT"

    h = login(client, org.priya)
    pending = client.get("/api/corrections", params={"scope": "review", "status": "PENDING"}).json()
    assert [c["id"] for c in pending["items"]] == [cid]
    r = client.post(f"/api/corrections/{cid}/approve", headers=h, json={"comment": "Confirmed"})
    assert r.status_code == 200
    assert r.json()["status"] == "APPROVED" and r.json()["reviewed_by"]["full_name"] == "Priya Sharma"

    db.expire_all()
    sessions = list(db.scalars(select(AttendanceSession).order_by(AttendanceSession.id)))
    assert sessions[0].status == SessionStatus.VOIDED and sessions[0].void_reason  # history preserved
    assert sessions[1].status == SessionStatus.COMPLETED and sessions[1].source == SessionSource.CORRECTION
    assert sessions[1].net_minutes == 480

    day = client.get(f"/api/attendance/employees/{org.rahul.id}/days/2026-03-09").json()
    assert day["record"]["status"] == "PRESENT" and day["record"]["net_minutes"] == 480
    assert len(day["voided_sessions"]) == 1

    actions = [a.action for a in db.scalars(select(AuditLog).order_by(AuditLog.id))]
    assert {"CORRECTION_SUBMITTED", "SESSION_VOIDED", "CORRECTION_APPROVED"} <= set(actions)
    approved = db.scalar(select(AuditLog).where(AuditLog.action == "CORRECTION_APPROVED"))
    assert approved.actor_user_id == org.priya.id
    assert approved.before_values["status"] == "PENDING" and approved.after_values["status"] == "APPROVED"


def test_add_missing_day(client, org):
    h = login(client, org.rahul)
    r = submit(client, h)
    assert r.status_code == 201 and r.json()["request_type"] == "ADD_SESSION"


def test_reject_requires_comment_and_keeps_attendance(client, org, db):
    h = login(client, org.rahul)
    cid = submit(client, h).json()["id"]
    h = login(client, org.priya)
    assert client.post(f"/api/corrections/{cid}/reject", headers=h, json={}).status_code == 422
    r = client.post(f"/api/corrections/{cid}/reject", headers=h, json={"comment": "No evidence"})
    assert r.json()["status"] == "REJECTED"
    assert db.scalar(select(AttendanceSession.id)) is None
    again = client.post(f"/api/corrections/{cid}/approve", headers=h, json={})
    assert again.status_code == 409


def test_manager_cannot_review_outside_scope(client, org):
    h = login(client, org.arjun)
    cid = submit(client, h).json()["id"]
    h = login(client, org.priya)
    assert client.post(f"/api/corrections/{cid}/approve", headers=h, json={}).status_code == 404


def test_nested_manager_and_admin_can_review(client, org):
    h = login(client, org.rahul)
    c1 = submit(client, h).json()["id"]
    c2 = submit(client, h, attendance_date="2026-03-06").json()["id"]
    h = login(client, org.vikram)
    assert client.post(f"/api/corrections/{c1}/approve", headers=h, json={}).status_code == 200
    h = login(client, org.admin)
    assert client.post(f"/api/corrections/{c2}/approve", headers=h, json={}).status_code == 200


def test_cannot_review_own_request(client, org):
    h = login(client, org.priya)
    cid = submit(client, h).json()["id"]
    assert client.post(f"/api/corrections/{cid}/approve", headers=h, json={}).status_code == 403


def test_policy_can_restrict_approvals_to_admins(client, org):
    h = login(client, org.admin)
    assert client.put("/api/admin/policy", headers=h, json={"manager_can_approve": False}).status_code == 200
    h = login(client, org.rahul)
    cid = submit(client, h).json()["id"]
    h = login(client, org.priya)
    assert client.post(f"/api/corrections/{cid}/approve", headers=h, json={}).status_code == 403


def test_invalid_correction_requests(client, org, db):
    h = login(client, org.rahul)
    assert submit(client, h, attendance_date="2026-03-11").status_code == 422          # future
    assert submit(client, h, attendance_date="2025-12-01").status_code == 422          # outside window
    assert submit(client, h, check_out_time="09:00").status_code == 422               # out before in
    assert submit(client, h, break_minutes=600).status_code == 422                    # break > duration
    assert submit(client, h, reason="x").status_code == 422                           # no reason
    assert submit(client, h, attendance_date="2026-03-10", check_out_time="18:30").status_code == 422  # future out
    other = missing_session(db, org.sneha)
    assert submit(client, h, session_id=other.id).status_code == 404                  # someone else's


def test_overlapping_correction_rejected(client, org, db):
    db.add(AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 9, 4, 0, tzinfo=UTC),
                             check_out_at=datetime(2026, 3, 9, 8, 0, tzinfo=UTC), net_minutes=240,
                             status=SessionStatus.COMPLETED, source=SessionSource.WEB))
    db.commit()
    h = login(client, org.rahul)
    r = submit(client, h, check_in_time="12:00", check_out_time="16:00", break_minutes=0)
    assert r.status_code == 422 and "overlap" in r.json()["error"]["message"]
    ok = submit(client, h, check_in_time="14:00", check_out_time="18:00", break_minutes=0)  # after 13:30 IST
    assert ok.status_code == 201


def test_duplicate_pending_correction_for_session(client, org, db):
    s = missing_session(db, org.rahul)
    h = login(client, org.rahul)
    assert submit(client, h, session_id=s.id).status_code == 201
    assert submit(client, h, session_id=s.id).status_code == 409


def test_cancel_own_pending(client, org):
    h = login(client, org.rahul)
    cid = submit(client, h).json()["id"]
    assert client.post(f"/api/corrections/{cid}/cancel", headers=h).json()["status"] == "CANCELLED"


def test_overnight_correction(client, org):
    h = login(client, org.rahul)
    r = submit(client, h, attendance_date="2026-03-06", check_in_time="21:00", check_out_time="05:00",
               check_out_next_day=True, break_minutes=30)
    assert r.status_code == 201
    cid = r.json()["id"]
    h = login(client, org.priya)
    client.post(f"/api/corrections/{cid}/approve", headers=h, json={})
    day = client.get(f"/api/attendance/employees/{org.rahul.id}/days/2026-03-06").json()
    assert day["record"]["net_minutes"] == 450


def test_leave_endpoints_are_gone(client, org):
    """Leave was removed: attendance is office presence only."""
    h = login(client, org.rahul)
    assert client.get("/api/leaves").status_code == 404
    assert client.post("/api/leaves", headers=h, json={"start_date": "2026-03-02", "end_date": "2026-03-03",
                                                       "leave_type": "SICK"}).status_code == 404
    summary = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                         params={"start": "2026-03-02", "end": "2026-03-03"}).json()
    assert "leave_days" not in summary and summary["absent_days"] == 2


def test_correction_inside_active_session_rejected(client, frozen, org, db):
    """A running session occupies [check-in, now); corrections may not be placed inside it."""
    frozen.set_local(date(2026, 3, 10), time(9, 0))
    h = login(client, org.rahul)
    assert client.post("/api/attendance/check-in", headers=h).status_code == 201
    frozen.set_local(date(2026, 3, 10), time(10, 30))
    h = login(client, org.rahul)
    r = submit(client, h, attendance_date="2026-03-10", check_in_time="09:30", check_out_time="10:00", break_minutes=0)
    assert r.status_code == 422 and "overlap" in r.json()["error"]["message"]
