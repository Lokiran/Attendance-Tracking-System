from concurrent.futures import ThreadPoolExecutor
from datetime import date, time

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select

from app.main import app
from app.models import AttendanceSession, SessionSource, SessionStatus
from app.tests.conftest import login

TUE = date(2026, 3, 10)


def test_check_in_and_check_out_flow(client, frozen, org):
    headers = login(client, org.rahul)
    frozen.set_local(TUE, time(9, 30))
    r = client.post("/api/attendance/check-in", headers=headers)
    assert r.status_code == 201
    assert r.json()["status"] == "OPEN"

    cur = client.get("/api/attendance/current").json()
    assert cur["active_session"]["id"] == r.json()["id"]
    assert cur["today_record"]["status"] == "PRESENT"
    assert cur["schedule"]["required_minutes"] == 480

    frozen.set_local(TUE, time(18, 45))
    headers = login(client, org.rahul)  # the long gap exceeds the idle timeout
    r = client.post("/api/attendance/check-out", headers=headers, json={"break_minutes": 45})
    assert r.status_code == 200
    assert r.json()["status"] == "COMPLETED"
    assert r.json()["net_minutes"] == 9 * 60 + 15 - 45

    summary = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                         params={"start": "2026-03-10", "end": "2026-03-10"}).json()
    assert summary["present_days"] == 1 and summary["net_minutes"] == 510
    assert summary["variance_minutes"] == 30


def test_server_time_is_authoritative(client, frozen, org):
    headers = login(client, org.rahul)
    frozen.set_local(TUE, time(9, 0))
    r = client.post("/api/attendance/check-in", headers=headers,
                    json={"check_in_at": "2020-01-01T00:00:00Z"})  # ignored
    assert r.json()["check_in_at"].startswith("2026-03-10T03:30")


def test_duplicate_check_in_rejected(client, frozen, org):
    headers = login(client, org.rahul)
    assert client.post("/api/attendance/check-in", headers=headers).status_code == 201
    r = client.post("/api/attendance/check-in", headers=headers)
    assert r.status_code == 409 and r.json()["error"]["code"] == "already_checked_in"


def test_check_out_without_session_rejected(client, org):
    headers = login(client, org.rahul)
    r = client.post("/api/attendance/check-out", headers=headers)
    assert r.status_code == 409 and r.json()["error"]["code"] == "not_checked_in"


def test_break_longer_than_session_rejected(client, frozen, org):
    headers = login(client, org.rahul)
    client.post("/api/attendance/check-in", headers=headers)
    frozen.advance(minutes=30)
    r = client.post("/api/attendance/check-out", headers=headers, json={"break_minutes": 45})
    assert r.status_code == 422


def test_multiple_sessions_same_day(client, frozen, org):
    for start, end in ((time(9, 30), time(13, 0)), (time(14, 0), time(18, 30))):
        frozen.set_local(TUE, start)
        headers = login(client, org.rahul)
        assert client.post("/api/attendance/check-in", headers=headers).status_code == 201
        frozen.set_local(TUE, end)
        headers = login(client, org.rahul)
        assert client.post("/api/attendance/check-out", headers=headers).status_code == 200
    day = client.get(f"/api/attendance/employees/{org.rahul.id}/days/2026-03-10").json()
    assert len(day["sessions"]) == 2
    assert day["record"]["net_minutes"] == 210 + 270
    assert day["record"]["completed_sessions"] == 2
    summary = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                         params={"start": "2026-03-10", "end": "2026-03-10"}).json()
    assert summary["present_days"] == 1


def test_forgotten_session_becomes_missing_checkout_and_new_day_can_start(client, frozen, org, db):
    headers = login(client, org.rahul)
    frozen.set_local(date(2026, 3, 9), time(9, 30))
    client.post("/api/attendance/check-in", headers=headers)
    frozen.set_local(TUE, time(9, 35))
    headers = login(client, org.rahul)
    r = client.post("/api/attendance/check-in", headers=headers)
    assert r.status_code == 201
    statuses = sorted(s.status for s in db.scalars(select(AttendanceSession)))
    assert statuses == [SessionStatus.MISSING_CHECKOUT, SessionStatus.OPEN]
    day = client.get(f"/api/attendance/employees/{org.rahul.id}/days",
                     params={"start": "2026-03-09", "end": "2026-03-09"}).json()["items"][0]
    assert day["status"] == "INCOMPLETE" and "MISSING_CHECKOUT" in day["exceptions"]


def test_check_out_after_max_session_length_is_refused(client, frozen, org, db):
    headers = login(client, org.rahul)
    frozen.set_local(TUE, time(6, 0))
    client.post("/api/attendance/check-in", headers=headers)
    frozen.set_local(TUE, time(23, 0))  # 17h > 16h max
    headers = login(client, org.rahul)
    r = client.post("/api/attendance/check-out", headers=headers)
    assert r.status_code == 409 and r.json()["error"]["code"] == "session_expired"
    assert db.scalar(select(AttendanceSession.status)) == SessionStatus.MISSING_CHECKOUT


def test_overnight_session_check_out_next_morning(client, frozen, org):
    frozen.set_local(TUE, time(21, 0))
    headers = login(client, org.rahul)
    assert client.post("/api/attendance/check-in", headers=headers).status_code == 201
    frozen.set_local(date(2026, 3, 11), time(5, 30))
    headers = login(client, org.rahul)
    r = client.post("/api/attendance/check-out", headers=headers, json={"break_minutes": 30})
    assert r.status_code == 200 and r.json()["net_minutes"] == 480
    days = client.get(f"/api/attendance/employees/{org.rahul.id}/days",
                      params={"start": "2026-03-10", "end": "2026-03-11", "order": "asc"}).json()["items"]
    assert days[0]["net_minutes"] == 480 and days[1]["net_minutes"] == 0


def test_inactive_employee_cannot_check_in(client, org, db):
    headers = login(client, org.rahul)
    org.rahul.is_active = False
    db.merge(org.rahul)
    db.commit()
    r = client.post("/api/attendance/check-in", headers=headers)
    assert r.status_code == 401  # session invalidated for inactive accounts


def test_history_pagination_and_invalid_range(client, org):
    login(client, org.rahul)
    r = client.get(f"/api/attendance/employees/{org.rahul.id}/days",
                   params={"start": "2026-03-01", "end": "2026-03-10", "page_size": 3})
    body = r.json()
    assert body["total"] == 10 and len(body["items"]) == 3
    assert body["items"][0]["date"] == "2026-03-10"
    bad = client.get(f"/api/attendance/employees/{org.rahul.id}/days", params={"start": "2026-03-10",
                                                                              "end": "2026-03-01"})
    assert bad.status_code == 422
    too_long = client.get(f"/api/attendance/employees/{org.rahul.id}/days",
                          params={"start": "2024-01-01", "end": "2026-03-01"})
    assert too_long.status_code == 422


def test_empty_period_returns_zeroes_not_fabricated_data(client, org):
    login(client, org.rahul)
    s = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                   params={"start": "2026-03-07", "end": "2026-03-08"}).json()  # weekend
    assert s["expected_days"] == 0 and s["attendance_percentage"] is None and s["net_minutes"] == 0


def test_db_rejects_second_open_session(db, org):
    from datetime import UTC, datetime

    from sqlalchemy.exc import IntegrityError
    db.add(AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 9, 4, tzinfo=UTC),
                             status=SessionStatus.OPEN, source=SessionSource.WEB))
    db.commit()
    db.add(AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 9, 6, tzinfo=UTC),
                             status=SessionStatus.OPEN, source=SessionSource.WEB))
    with pytest.raises(IntegrityError):
        db.commit()
    db.rollback()


def test_db_rejects_checkout_before_checkin(db, org):
    from datetime import UTC, datetime

    from sqlalchemy.exc import IntegrityError
    db.add(AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 9, 6, tzinfo=UTC),
                             check_out_at=datetime(2026, 3, 9, 5, tzinfo=UTC), net_minutes=0,
                             status=SessionStatus.COMPLETED, source=SessionSource.WEB))
    with pytest.raises(IntegrityError):
        db.commit()
    db.rollback()


def test_concurrent_check_ins_create_only_one_session(frozen, org, db):
    clients = [TestClient(app) for _ in range(4)]
    headers = [login(c, org.rahul) for c in clients]

    def go(i):
        return clients[i].post("/api/attendance/check-in", headers=headers[i]).status_code

    with ThreadPoolExecutor(max_workers=4) as pool:
        codes = list(pool.map(go, range(4)))
    assert codes.count(201) == 1
    assert all(c in (201, 409, 500) for c in codes) and 500 not in codes
    assert db.scalar(select(func.count()).select_from(AttendanceSession)) == 1


def test_current_status_only_offers_correctable_missing_checkout(client, frozen, org, db):
    from datetime import UTC, datetime

    old = AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 1, 5, 4, tzinfo=UTC),
                            status=SessionStatus.MISSING_CHECKOUT, source=SessionSource.WEB)
    db.add(old)
    db.commit()
    login(client, org.rahul)
    assert client.get("/api/attendance/current").json()["unresolved_session"] is None  # outside 30-day window
    recent = AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 9, 4, tzinfo=UTC),
                               status=SessionStatus.MISSING_CHECKOUT, source=SessionSource.WEB)
    db.add(recent)
    db.commit()
    assert client.get("/api/attendance/current").json()["unresolved_session"]["id"] == recent.id


def test_check_out_refused_if_it_would_overlap_a_recorded_session(client, frozen, org, db):
    from datetime import UTC, datetime

    frozen.set_local(TUE, time(9, 0))
    h = login(client, org.rahul)
    assert client.post("/api/attendance/check-in", headers=h).status_code == 201
    # A completed session inside the running one (only possible by bypassing the services).
    db.add(AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 10, 4, 30, tzinfo=UTC),
                             check_out_at=datetime(2026, 3, 10, 5, 30, tzinfo=UTC), net_minutes=60,
                             status=SessionStatus.COMPLETED, source=SessionSource.ADMIN))
    db.commit()
    frozen.set_local(TUE, time(10, 30))
    h = login(client, org.rahul)
    r = client.post("/api/attendance/check-out", headers=h)
    assert r.status_code == 409 and r.json()["error"]["code"] == "session_overlap"
    db.expire_all()
    assert db.scalar(select(func.count()).select_from(AttendanceSession)
                     .where(AttendanceSession.status == SessionStatus.OPEN)) == 1
