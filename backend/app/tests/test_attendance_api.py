"""Attendance API: everything comes from imported timesheets; check-in / check-out and corrections are gone."""

from datetime import UTC, date, datetime, time

import pytest
from sqlalchemy.exc import IntegrityError

from app.models import AttendanceSession, SessionSource, SessionStatus, WorkMode
from app.tests.conftest import login

URL = "/api/admin/attendance-uploads"


def import_csv(client, h, csv: str) -> dict:
    up = client.post(URL, headers=h, files={"file": ("timesheet.csv", csv.encode(), "text/csv")}).json()
    r = client.post(f"{URL}/{up['id']}/commit", headers=h, json={})
    assert r.status_code == 200, r.text
    return r.json()


def test_check_in_check_out_and_corrections_are_gone(client, org):
    h = login(client, org.rahul)
    for path in ("/api/attendance/check-in", "/api/attendance/check-out", "/api/corrections"):
        assert client.post(path, headers=h, json={}).status_code in (404, 405)
    assert client.get("/api/corrections").status_code == 404


def test_monthly_timesheet_drives_attendance(client, frozen, org):
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    h = login(client, org.admin)
    rahul = org.rahul.employee_code
    # September presence list: Rahul in on 1, 2 and 3 Sept; WFH on 4 Sept (a separate WFH file).
    up = import_csv(client, h, f"Employee ID,Date\n{rahul},01/09/2026\n{rahul},02/09/2026\n{rahul},03/09/2026\n")
    assert (up["period_start"], up["period_end"]) == ("2026-09-01", "2026-09-30")
    wfh = client.post(URL, headers=h, params={"presence_mode": "WFH"},
                      files={"file": ("wfh.csv", f"Employee ID,Date\n{rahul},04/09/2026\n".encode(), "text/csv")})
    client.post(f"{URL}/{wfh.json()['id']}/commit", headers=h, json={})

    login(client, org.rahul)
    s = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                   params={"start": "2026-09-01", "end": "2026-09-30"}).json()
    assert s["present_days"] == 3 and s["wfh_days"] == 1 and s["expected_days"] == 22
    assert s["absent_days"] == 18 and s["no_timesheet_days"] == 0
    assert s["hours_days"] == 0 and s["average_net_minutes"] is None  # a presence list has no hours
    assert s["wfo"]["wfo_days"] == 3

    october = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                         params={"start": "2026-10-01", "end": "2026-10-31"}).json()
    assert october["absent_days"] == 0 and october["no_timesheet_days"] == 22  # no October sheet yet
    assert october["attendance_percentage"] is None

    days = client.get(f"/api/attendance/employees/{org.rahul.id}/days",
                      params={"start": "2026-09-01", "end": "2026-09-07", "order": "asc"}).json()["items"]
    assert [d["status"] for d in days] == ["PRESENT", "PRESENT", "PRESENT", "WFH", "WEEKEND", "WEEKEND", "ABSENT"]
    assert days[0]["hours_known"] is False and days[0]["work_mode"] == "OFFICE"

    cur = client.get("/api/attendance/current").json()
    assert cur["timesheet_through"] == "2026-09-30" and cur["schedule"]["required_minutes"] == 480
    assert "active_session" not in cur


def test_hours_and_times_from_the_timesheet(client, frozen, org):
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    h = login(client, org.admin)
    code = org.sneha.employee_code
    import_csv(client, h, f"Employee ID,Date,In,Out\n{code},01/10/2026,10:05,17:05\n{code},02/10/2026,09:30,18:30\n")
    login(client, org.sneha)
    day = client.get(f"/api/attendance/employees/{org.sneha.id}/days/2026-10-01").json()
    rec = day["record"]
    assert rec["status"] == "PRESENT" and rec["net_minutes"] == 420 and rec["hours_known"]
    assert set(rec["exceptions"]) == {"LATE_ARRIVAL", "SHORT_HOURS"}
    assert day["timesheet"]["upload_filename"] == "timesheet.csv" and day["timesheet"]["work_mode"] == "OFFICE"
    s = client.get(f"/api/attendance/employees/{org.sneha.id}/summary",
                   params={"start": "2026-10-01", "end": "2026-10-02"}).json()
    # 7 h on the 1st and 9 h on the 2nd against two 8 h shifts.
    assert (s["net_minutes"], s["hours_required_minutes"], s["variance_minutes"]) == (960, 960, 0)
    assert s["hours_days"] == 2 and s["average_net_minutes"] == 480 and s["late_days"] == 1


def test_partial_month_sheet_covers_up_to_the_day_before_import(client, frozen, org):
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    h = login(client, org.admin)
    up = import_csv(client, h, f"Employee ID,Date\n{org.rahul.employee_code},03/11/2026\n")
    assert (up["period_start"], up["period_end"]) == ("2026-11-01", "2026-11-09")
    login(client, org.rahul)
    days = {d["date"]: d["status"] for d in client.get(
        f"/api/attendance/employees/{org.rahul.id}/days",
        params={"start": "2026-11-02", "end": "2026-11-10"}).json()["items"]}
    assert days["2026-11-03"] == "PRESENT" and days["2026-11-09"] == "ABSENT" and days["2026-11-10"] == "NO_TIMESHEET"


def test_a_timesheet_only_covers_the_people_listed_in_it(client, frozen, org):
    """A partial file (one team, one correction) must not make everyone else absent."""
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    h = login(client, org.admin)
    import_csv(client, h, f"Employee ID,Date\n{org.rahul.employee_code},01/10/2026\n")
    s = client.get(f"/api/attendance/employees/{org.sneha.id}/summary",
                   params={"start": "2026-10-01", "end": "2026-10-31"}).json()
    assert s["absent_days"] == 0 and s["no_timesheet_days"] == 22
    r = client.get(f"/api/attendance/employees/{org.rahul.id}/summary",
                   params={"start": "2026-10-01", "end": "2026-10-31"}).json()
    assert r["present_days"] == 1 and r["absent_days"] == 21
    assert client.get("/api/attendance/current").json()["timesheet_through"] == "2026-10-31"
    login(client, org.sneha)
    cur = client.get("/api/attendance/current").json()
    assert cur["timesheet_through"] == "2026-10-31" and cur["my_timesheet_through"] is None


def test_admin_override_counts_without_a_sheet(client, frozen, org, factory):
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    h = login(client, org.admin)
    r = client.put(f"/api/admin/attendance/{org.arjun.id}/2026-11-04/work-mode", headers=h,
                   json={"work_mode": "OFFICE", "note": "Client visit"})
    assert r.status_code == 200
    day = client.get(f"/api/attendance/employees/{org.arjun.id}/days/2026-11-04").json()
    assert day["record"]["status"] == "PRESENT" and day["timesheet"]["override_note"] == "Client visit"
    factory.timesheet(org.arjun, date(2026, 11, 5), WorkMode.LEAVE)
    assert client.get(f"/api/attendance/employees/{org.arjun.id}/days/2026-11-05").json()["record"]["status"] \
        == "LEAVE"


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


# The check-in tables are kept (unused) with their database rules intact.

def test_legacy_session_table_still_rejects_second_open_session(db, org):
    db.add(AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 9, 4, tzinfo=UTC),
                             status=SessionStatus.OPEN, source=SessionSource.WEB))
    db.commit()
    db.add(AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 9, 6, tzinfo=UTC),
                             status=SessionStatus.OPEN, source=SessionSource.WEB))
    with pytest.raises(IntegrityError):
        db.commit()
    db.rollback()


def test_legacy_sessions_do_not_count_as_attendance(client, frozen, org, db, factory):
    frozen.set_local(date(2026, 3, 13), time(12, 0))
    factory.cover(date(2026, 3, 1), date(2026, 3, 12), [org.rahul])
    db.add(AttendanceSession(employee_id=org.rahul.id, check_in_at=datetime(2026, 3, 10, 4, tzinfo=UTC),
                             check_out_at=datetime(2026, 3, 10, 12, tzinfo=UTC), net_minutes=480,
                             status=SessionStatus.COMPLETED, source=SessionSource.WEB))
    db.commit()
    login(client, org.rahul)
    day = client.get(f"/api/attendance/employees/{org.rahul.id}/days/2026-03-10").json()
    assert day["record"]["status"] == "ABSENT" and day["timesheet"] is None
