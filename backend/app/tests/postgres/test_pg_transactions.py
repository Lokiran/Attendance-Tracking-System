"""Transaction boundaries and timestamptz handling on PostgreSQL."""

from datetime import UTC, date, datetime, time

from sqlalchemy import func, select, text

from app.db.session import engine
from app.models import AttendanceCorrection, AttendanceSession, AuditLog, RequestStatus, SessionStatus
from app.services import corrections
from app.tests.conftest import login

from .conftest import INSERT_SESSION, session_row

TUE = date(2026, 3, 10)


def test_driver_is_psycopg3():
    assert engine.dialect.name == "postgresql" and engine.dialect.driver == "psycopg"


def test_check_in_out_roundtrip_is_utc_regardless_of_session_timezone(client, frozen, org, db):
    frozen.set_local(TUE, time(9, 15))
    h = login(client, org.rahul)
    assert client.post("/api/attendance/check-in", headers=h).status_code == 201
    frozen.set_local(TUE, time(18, 0))
    h = login(client, org.rahul)
    assert client.post("/api/attendance/check-out", headers=h, json={"break_minutes": 45}).json()["net_minutes"] == 480

    expected_in = datetime(2026, 3, 10, 3, 45, tzinfo=UTC)  # 09:15 IST
    expected_out = datetime(2026, 3, 10, 12, 30, tzinfo=UTC)  # 18:00 IST
    with engine.connect() as conn:
        conn.execute(text("SET TIME ZONE 'America/New_York'"))  # server-side display zone must not matter
        raw_in, raw_out = conn.execute(text(
            "SELECT check_in_at AT TIME ZONE 'UTC', check_out_at AT TIME ZONE 'UTC' FROM attendance_sessions")).one()
    assert raw_in == expected_in.replace(tzinfo=None) and raw_out == expected_out.replace(tzinfo=None)

    db.execute(text("SET TIME ZONE 'Pacific/Auckland'"))
    s = db.scalar(select(AttendanceSession))
    assert s.check_in_at == expected_in and s.check_in_at.utcoffset().total_seconds() == 0
    day = client.get(f"/api/attendance/employees/{org.rahul.id}/days/2026-03-10").json()
    assert day["record"]["net_minutes"] == 480  # attributed to the office-local day


def test_rejected_check_out_leaves_session_untouched(client, frozen, org, db):
    h = login(client, org.rahul)
    client.post("/api/attendance/check-in", headers=h)
    frozen.advance(minutes=20)
    assert client.post("/api/attendance/check-out", headers=h, json={"break_minutes": 30}).status_code == 422
    db.expire_all()
    s = db.scalar(select(AttendanceSession))
    assert s.status == SessionStatus.OPEN and s.check_out_at is None and s.net_minutes is None


def test_expired_session_flag_is_committed_even_though_request_fails(client, frozen, org, db):
    frozen.set_local(TUE, time(6, 0))
    h = login(client, org.rahul)
    client.post("/api/attendance/check-in", headers=h)
    frozen.set_local(TUE, time(23, 0))
    h = login(client, org.rahul)
    r = client.post("/api/attendance/check-out", headers=h)
    assert r.status_code == 409 and r.json()["error"]["code"] == "session_expired"
    db.expire_all()
    assert db.scalar(select(AttendanceSession.status)) == SessionStatus.MISSING_CHECKOUT


def test_correction_approval_rolls_back_entirely_when_exclusion_constraint_fires(client, frozen, org, db, monkeypatch):
    """Bypass the service-level overlap check so only PostgreSQL can stop the overlap; the
    approval must then fail with 409 and leave no partial changes (void, new session, audit)."""
    missing = session_row(org.rahul.id, datetime(2026, 3, 9, 4, 0, tzinfo=UTC), status="MISSING_CHECKOUT")
    afternoon = session_row(org.rahul.id, datetime(2026, 3, 9, 8, 30, tzinfo=UTC), datetime(2026, 3, 9, 10, 30, tzinfo=UTC))
    with engine.begin() as conn:
        conn.execute(INSERT_SESSION, missing)
        conn.execute(INSERT_SESSION, afternoon)
    original_id = db.scalar(select(AttendanceSession.id).where(AttendanceSession.status == SessionStatus.MISSING_CHECKOUT))
    monkeypatch.setattr(corrections, "_overlaps", lambda *a, **k: False)

    h = login(client, org.rahul)
    r = client.post("/api/corrections", headers=h, json={
        "attendance_date": "2026-03-09", "session_id": original_id, "check_in_time": "09:30",
        "check_out_time": "18:00", "break_minutes": 0, "reason": "Forgot to check out"})
    assert r.status_code == 201
    cid = r.json()["id"]

    h = login(client, org.priya)
    r = client.post(f"/api/corrections/{cid}/approve", headers=h, json={})
    assert r.status_code == 409, r.text

    db.expire_all()
    assert db.get(AttendanceSession, original_id).status == SessionStatus.MISSING_CHECKOUT  # void rolled back
    assert db.scalar(select(func.count()).select_from(AttendanceSession)) == 2
    assert db.get(AttendanceCorrection, cid).status == RequestStatus.PENDING
    assert db.scalar(select(func.count()).select_from(AuditLog).where(
        AuditLog.action.in_(["SESSION_VOIDED", "CORRECTION_APPROVED"]))) == 0
