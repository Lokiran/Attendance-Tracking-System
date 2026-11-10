"""WFO uploads: parsing, preview, commit (upsert), override protection, badge conflicts and the sample files."""

from datetime import date, datetime, time
from pathlib import Path

import pytest
from sqlalchemy import func, select

from app.core.errors import ValidationFailed
from app.models import AttendanceDaily, AuditLog, Role, User
from app.services.wfo_upload import parse_date, parse_hours, parse_time, read_table
from app.tests.conftest import login

SAMPLES = Path(__file__).resolve().parents[3] / "samples"
CODES = ["EMP001", "EMP002", "EMP003", "EMP004", "EMP005", "EMP006", "EMP007", "EMP009", "EMP010"]
URL = "/api/admin/attendance-uploads"


@pytest.fixture
def staff(factory, db, frozen):
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    factory.default_schedule()
    admin = factory.user("Hr Admin", Role.ADMIN)
    admin.attendance_tracked = False
    manager = factory.user("Team Lead", Role.MANAGER)
    people = {}
    for code in CODES:
        u = factory.user(f"Person {code}", manager=manager, joined=date(2026, 1, 1))
        u.employee_code = code
        people[code] = u
    db.commit()
    return type("Staff", (), {"admin": admin, "manager": manager, "people": people})


def upload(client, h, name: str, content: bytes, mime="text/csv"):
    return client.post(URL, headers=h, files={"file": (name, content, mime)})


def sample(name: str) -> bytes:
    return (SAMPLES / name).read_bytes()


def wfo_month(client, emp_id: int, month="2026-10") -> dict:
    return client.get(f"/api/team/wfo/employees/{emp_id}", params={"month": month}).json()


def day(body: dict, d: str) -> dict:
    return next(x for x in body["days"] if x["date"] == d)


# ------------------------------------------------------------------ parsing

def test_value_parsers():
    assert parse_date("31/03/2026") == parse_date("31-03-2026") == parse_date("2026-03-31") == date(2026, 3, 31)
    assert parse_date("31-Mar-2026") == date(2026, 3, 31) and parse_date(datetime(2026, 3, 31, 9)) == date(2026, 3, 31)
    assert parse_date("03/31/2026") is None  # day-first only
    assert parse_hours("7.5") == parse_hours(7.5) == parse_hours("7:30") == 450 and parse_hours("") is None
    with pytest.raises(ValueError):
        parse_hours("30")
    assert parse_time("09:30") == time(9, 30) and parse_time("6:15 PM") == time(18, 15)
    with pytest.raises(ValueError):
        parse_time("25:99")


def test_csv_delimiters_and_file_checks():
    assert read_table("a.csv", "Employee;Date\nEMP001;2026-10-05\n".encode())[1] == ["EMP001", "2026-10-05"]
    with pytest.raises(ValidationFailed, match=".csv or .xlsx"):
        read_table("a.pdf", b"x")
    with pytest.raises(ValidationFailed, match="empty"):
        read_table("a.csv", b"")
    with pytest.raises(ValidationFailed, match="larger"):
        read_table("a.csv", b"x" * (5 * 1024 * 1024 + 1))


def test_header_problems_are_reported(client, staff):
    h = login(client, staff.admin)
    r = upload(client, h, "x.csv", b"Name,Day\nA,2026-10-05\n")
    assert r.status_code == 422 and "Missing column(s): employee" in r.json()["error"]["message"]
    r = upload(client, h, "x.csv", b"Employee,Date\nEMP001,2026-10-05\n")
    assert r.status_code == 422 and "nothing to import" in r.json()["error"]["message"]


# ------------------------------------------------------------------ sample files: preview and commit

def test_sample_csv_preview_counts_office_wfh_and_conflicts(client, staff, db):
    h = login(client, staff.admin)
    r = upload(client, h, "wfo_attendance_sample.csv", sample("wfo_attendance_sample.csv"))
    assert r.status_code == 201, r.text
    body = r.json()
    s = body["summary"]
    assert body["status"] == "PREVIEW" and body["row_count"] == 182 and body["error_count"] == 0
    assert s["badge_rows"] == 2 and s["employees"] == 9 and s["employee_days"] == 180
    assert s["office"] > 0 and s["wfh"] > 0 and s["by_work_mode"].get("LEAVE") == 1
    assert s["office"] + s["wfh"] + 1 == 180
    assert s["conflict_count"] == 2  # EMP003: HR said WFH, badge says office (8 and 15 Oct)
    assert {c["date"] for c in s["conflicts"]} == {"2026-10-08", "2026-10-15"}
    assert s["office_below_minimum"] == 4  # EMP004's 3.5 h Mondays
    assert s["columns"]["work_mode"] == "Work Mode"
    assert db.scalar(select(func.count()).select_from(AttendanceDaily)) == 0  # preview applies nothing


def test_commit_applies_and_classifies(client, staff, db):
    h = login(client, staff.admin)
    up = upload(client, h, "s.csv", sample("wfo_attendance_sample.csv")).json()
    r = client.post(f"{URL}/{up['id']}/commit", headers=h, json={})
    assert r.status_code == 200 and r.json()["status"] == "COMMITTED"
    assert r.json()["commit_summary"]["applied_days"] == 180
    assert db.scalar(select(func.count()).select_from(AttendanceDaily)) == 180

    emp3 = wfo_month(client, staff.people["EMP003"].id)
    d = day(emp3, "2026-10-08")
    assert (d["work_mode"], d["wfo_source"]) == ("OFFICE", "BADGE") and d["conflicts"]
    emp1 = wfo_month(client, staff.people["EMP001"].id)
    assert day(emp1, "2026-10-05")["wfo_source"] == "UPLOAD_COLUMN" and emp1["summary"]["wfo_days"] == 12
    emp4 = wfo_month(client, staff.people["EMP004"].id)
    assert day(emp4, "2026-10-05")["work_mode"] == "OFFICE" and not day(emp4, "2026-10-05")["is_wfo_day"]
    emp5 = wfo_month(client, staff.people["EMP005"].id)
    assert emp5["summary"]["wfo_days"] == 4 and emp5["summary"]["compliance"] == "NON_COMPLIANT"
    emp6 = wfo_month(client, staff.people["EMP006"].id)
    assert day(emp6, "2026-10-14")["work_mode"] == "LEAVE" and emp6["summary"]["leave_days"] == 1
    assert db.scalar(select(AuditLog).where(AuditLog.action == "ATTENDANCE_UPLOAD_COMMITTED")) is not None
    assert client.post(f"{URL}/{up['id']}/commit", headers=h, json={}).status_code == 409  # only once


def test_reupload_upserts_and_respects_manual_overrides(client, staff, db):
    h = login(client, staff.admin)
    first = upload(client, h, "s.csv", sample("wfo_attendance_sample.csv")).json()
    client.post(f"{URL}/{first['id']}/commit", headers=h, json={})
    emp2 = staff.people["EMP002"]
    client.put(f"/api/admin/attendance/{emp2.id}/2026-10-06/work-mode", headers=h, json={"work_mode": "WFH"})

    again = upload(client, h, "s.csv", sample("wfo_attendance_sample.csv")).json()
    assert again["summary"]["protected_overrides"] == 1 and again["summary"]["new_days"] == 0
    r = client.post(f"{URL}/{again['id']}/commit", headers=h, json={})
    assert r.json()["commit_summary"]["skipped_overrides"] == 1
    assert db.scalar(select(func.count()).select_from(AttendanceDaily)) == 180  # upsert, no duplicates
    d = day(wfo_month(client, emp2.id), "2026-10-06")
    assert (d["work_mode"], d["wfo_source"]) == ("WFH", "MANUAL_OVERRIDE")

    third = upload(client, h, "s.csv", sample("wfo_attendance_sample.csv")).json()
    r = client.post(f"{URL}/{third['id']}/commit", headers=h, json={"overwrite_overrides": True})
    assert r.json()["commit_summary"]["overrides_overwritten"] == 1
    d = day(wfo_month(client, emp2.id), "2026-10-06")
    assert (d["work_mode"], d["wfo_source"]) == ("OFFICE", "UPLOAD_COLUMN")
    cleared = db.scalar(select(AuditLog).where(AuditLog.action == "WORK_MODE_OVERRIDE_CLEARED"))
    assert cleared.after_values["via_upload"] == third["id"]


def test_badge_excel_turns_wfh_days_into_office_with_conflicts(client, staff):
    h = login(client, staff.admin)
    hr = upload(client, h, "s.csv", sample("wfo_attendance_sample.csv")).json()
    client.post(f"{URL}/{hr['id']}/commit", headers=h, json={})
    emp5 = staff.people["EMP005"]
    assert wfo_month(client, emp5.id)["summary"]["wfo_days"] == 4

    xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    badge = upload(client, h, "wfo_badge_sample.xlsx", sample("wfo_badge_sample.xlsx"), xlsx)
    assert badge.status_code == 201, badge.text
    s = badge.json()["summary"]
    assert badge.json()["error_count"] == 0 and s["badge_rows"] == badge.json()["row_count"]
    # EMP005 / EMP010 badge Mon-Thu but the HR file has only Tuesday as office: 3 days x 4 weeks x 2 people.
    assert s["conflict_count"] == 24
    client.post(f"{URL}/{badge.json()['id']}/commit", headers=h, json={})
    body = wfo_month(client, emp5.id)
    thursday = day(body, "2026-10-08")
    assert (thursday["work_mode"], thursday["wfo_source"]) == ("OFFICE", "BADGE")
    assert "UPLOAD_COLUMN said WFH" in thursday["conflicts"][0]
    assert body["summary"]["wfo_days"] == 16  # Monday to Thursday, four weeks
    # The HR file's work mode still decides days where both agree (upload column has priority).
    assert day(body, "2026-10-06")["wfo_source"] == "UPLOAD_COLUMN"


def test_row_errors_are_listed_and_skipped(client, staff, db):
    csv = (
        "Employee,Date,Mode,Hours\n"
        "EMP001,05/10/2026,WFO,8\n"         # ok
        "NOPE,05/10/2026,WFO,8\n"           # unknown employee
        "EMP001,2026/31/10,WFO,8\n"         # bad date
        "EMP002,05/10/2026,Hybrid,8\n"      # unknown work mode
        "EMP003,05/10/2026,WFO,30\n"        # impossible hours
        "EMP004,20/11/2026,WFO,8\n"         # future office day
        "EMP004,20/11/2026,Leave,\n"        # future leave is fine
        "emp005,06/10/2026,Remote,7.5\n"    # codes are case-insensitive
    )
    h = login(client, staff.admin)
    body = upload(client, h, "e.csv", csv.encode()).json()
    errors = {e["row"]: e["error"] for e in body["summary"]["errors"]}
    assert set(errors) == {3, 4, 5, 6, 7}
    assert "unknown employee" in errors[3] and "not a date" in errors[4] and "unknown work mode" in errors[5]
    assert "between 0 and 24" in errors[6] and "future" in errors[7]
    r = client.post(f"{URL}/{body['id']}/commit", headers=h, json={})
    assert r.json()["commit_summary"]["applied_days"] == 3
    assert db.scalar(select(func.count()).select_from(AttendanceDaily)) == 3


def test_upload_access_and_lifecycle(client, staff, db):
    h = login(client, staff.manager)
    assert upload(client, h, "s.csv", b"Employee,Date,Mode\nEMP001,05/10/2026,WFO\n").status_code == 403
    h = login(client, staff.people["EMP001"])
    assert upload(client, h, "s.csv", b"Employee,Date,Mode\nEMP001,05/10/2026,WFO\n").status_code == 403
    login(client, staff.admin)
    assert client.post(URL, files={"file": ("s.csv", b"x", "text/csv")}).status_code == 403  # no CSRF token
    h = login(client, staff.admin)
    up = upload(client, h, "s.csv", b"Employee,Date,Mode\nEMP001,05/10/2026,WFO\n").json()
    assert client.post(f"{URL}/{up['id']}/discard", headers=h).json()["status"] == "DISCARDED"
    assert client.post(f"{URL}/{up['id']}/commit", headers=h, json={}).status_code == 409
    listed = client.get(URL).json()
    assert listed[0]["id"] == up["id"] and listed[0]["uploaded_by_name"] == "Hr Admin"
    assert client.get(f"{URL}/999").status_code == 404
    assert db.scalar(select(User).where(User.employee_code == "EMP001")) is not None
