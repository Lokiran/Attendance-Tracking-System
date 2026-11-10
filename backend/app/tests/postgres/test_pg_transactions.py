"""Transaction boundaries and timestamptz handling on PostgreSQL (timesheet imports)."""

from datetime import UTC, date, datetime, time

from sqlalchemy import func, select, text

from app.db.session import engine
from app.models import AttendanceDaily, AttendanceUpload, UploadStatus
from app.services import wfo
from app.tests.conftest import login

URL = "/api/admin/attendance-uploads"


def test_driver_is_psycopg3():
    assert engine.dialect.name == "postgresql" and engine.dialect.driver == "psycopg"


def test_timesheet_times_roundtrip_as_utc_regardless_of_session_timezone(client, frozen, org, db):
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    h = login(client, org.admin)
    csv = f"Employee ID,Date,In,Out\n{org.rahul.employee_code},10/03/2026,09:15,18:00\n".encode()
    up = client.post(URL, headers=h, files={"file": ("t.csv", csv, "text/csv")}).json()
    assert client.post(f"{URL}/{up['id']}/commit", headers=h, json={}).status_code == 200

    expected_in = datetime(2026, 3, 10, 3, 45, tzinfo=UTC)  # 09:15 IST
    expected_out = datetime(2026, 3, 10, 12, 30, tzinfo=UTC)  # 18:00 IST
    with engine.connect() as conn:
        conn.execute(text("SET TIME ZONE 'America/New_York'"))  # server-side display zone must not matter
        raw_in, raw_out = conn.execute(text(
            "SELECT first_in AT TIME ZONE 'UTC', last_out AT TIME ZONE 'UTC' FROM attendance_daily")).one()
    assert raw_in == expected_in.replace(tzinfo=None) and raw_out == expected_out.replace(tzinfo=None)

    db.execute(text("SET TIME ZONE 'Pacific/Auckland'"))
    row = db.scalar(select(AttendanceDaily))
    assert row.first_in == expected_in and row.first_in.utcoffset().total_seconds() == 0
    day = client.get(f"/api/attendance/employees/{org.rahul.id}/days/2026-03-10").json()
    assert day["record"]["net_minutes"] == 525 and day["record"]["status"] == "PRESENT"


def test_failed_commit_rolls_back_entirely(client, frozen, org, db, monkeypatch):
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    h = login(client, org.admin)
    csv = f"Employee ID,Date\n{org.rahul.employee_code},01/10/2026\n{org.sneha.employee_code},01/10/2026\n".encode()
    up = client.post(URL, headers=h, files={"file": ("t.csv", csv, "text/csv")}).json()

    def boom(*_a, **_k):
        raise RuntimeError("storage failed half way")
    monkeypatch.setattr(wfo, "store", boom)
    try:
        client.post(f"{URL}/{up['id']}/commit", headers=h, json={})
    except RuntimeError:
        pass  # the test client re-raises server errors
    db.expire_all()
    assert db.scalar(select(func.count()).select_from(AttendanceDaily)) == 0
    assert db.get(AttendanceUpload, up["id"]).status == UploadStatus.PREVIEW  # can be committed again
