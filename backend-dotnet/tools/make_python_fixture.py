"""Build tests/ATS.Tests/Fixtures/python-0006.sqlite3: a SQLite database created by Alembic (head 0006) with one or
two rows per table written by the Python backend's own SQLAlchemy models. The .NET tests read it to prove they
understand what Python writes (timestamp / time text, JSON, enums, nulls) and adopt it with `ef-baseline`.

All data is fictional. Run from the repository root with the backend's virtual environment:
    backend\\.venv\\Scripts\\python.exe backend-dotnet\\tools\\make_python_fixture.py
"""
import os
import sys
from datetime import UTC, date, datetime, time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "backend-dotnet" / "tests" / "ATS.Tests" / "Fixtures" / "python-0006.sqlite3"
os.environ["DATABASE_URL"] = f"sqlite:///{OUT.as_posix()}"
os.environ["ATS_ENV_FILE"] = str(ROOT / "backend-dotnet" / "tools" / "fixture.env")
Path(os.environ["ATS_ENV_FILE"]).write_text(f"DATABASE_URL=sqlite:///{OUT.as_posix()}\nSECRET_KEY=fixture-only\n")
sys.path.insert(0, str(ROOT / "backend"))
os.chdir(ROOT / "backend")

from alembic import command  # noqa: E402
from sqlalchemy import create_engine  # noqa: E402
from sqlalchemy.orm import Session  # noqa: E402

from app.models import (  # noqa: E402
    AttendanceCorrection, AttendanceDaily, AttendanceDailyCorrection, AttendancePolicy, AttendanceSession,
    AttendanceUpload, AttendanceUploadRow, AuditLog, CorrectionType, DailyStatus, Department, Device, Holiday,
    LeaveRecord, LeaveType, LoginAttempt, OfficeSite, PresenceEvent, RequestStatus, Role, SessionSource,
    SessionStatus, UploadStatus, User, UserSession, WfoSource, WifiEvent, WifiEventType, WorkLocation, WorkMode,
    WorkSchedule,
)
from app.tests.migration_utils import alembic_config  # noqa: E402

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.unlink(missing_ok=True)
OUT.touch()
command.upgrade(alembic_config(os.environ["DATABASE_URL"]), "head")
Path(os.environ["ATS_ENV_FILE"]).unlink()

T = datetime(2026, 10, 5, 4, 52, 13, 123456, tzinfo=UTC)   # 10:22:13.123456 in the office (IST)
T2 = datetime(2026, 10, 5, 11, 30, tzinfo=UTC)              # no microseconds
engine = create_engine(os.environ["DATABASE_URL"])
with Session(engine) as db:
    dept = Department(name="Platform Engineering", description="Fictional team", created_at=T, updated_at=T)
    db.add(dept)
    db.flush()
    boss = User(employee_code="E1000", full_name="Asha Rao", email="asha.rao@example.test", password_hash="x",
                role=Role.MANAGER, job_title="Lead", department_id=dept.id, is_active=True, attendance_tracked=True,
                date_joined=date(2024, 1, 15), created_at=T, updated_at=T2)
    db.add(boss)
    db.flush()
    emp = User(employee_code="Intern - 90001", full_name="Ravi Kumar", email="ravi@imported.example.com",
               password_hash="$argon2id$v=19$m=65536,t=3,p=4$c2FsdHNhbHQ$aGFzaGhhc2g", role=Role.EMPLOYEE,
               department_id=dept.id, manager_id=boss.id, is_active=True, attendance_tracked=False,
               date_joined=date(2026, 6, 1), graph_user_id="0f8fad5b-d9cb-469f-a165-70867728950e",
               wifi_identity="aa:bb:cc:dd:ee:ff", created_at=T, updated_at=T)
    db.add(emp)
    db.flush()
    dept.department_head_id = boss.id
    db.add_all([
        WorkSchedule(name="Day shift", start_time=time(9, 30), end_time=time(18, 30), required_minutes=480,
                     working_days=[1, 2, 3, 4, 5], effective_from=date(2024, 1, 1), created_at=T, updated_at=T),
        WorkSchedule(name="Night support", employee_id=emp.id, start_time=time(22, 0, 0, 500000),
                     end_time=time(6, 0), required_minutes=420, working_days=[7, 1], effective_from=date(2026, 6, 1),
                     effective_to=date(2026, 12, 31), created_at=T, updated_at=T),
        Holiday(name="Gandhi Jayanti", holiday_date=date(2026, 10, 2), created_at=T),
        Holiday(name="Team day", holiday_date=date(2026, 10, 2), department_id=dept.id, created_at=T),
        AttendancePolicy(id=1, min_present_minutes=0, late_grace_minutes=15, updated_at=T, updated_by_id=boss.id,
                         wfh_presence_states=["Available", "Busy"], min_office_minutes_for_wfo_day=0,
                         notes="Policy note – with an en dash"),
    ])
    up = AttendanceUpload(filename="October 2026.pdf", file_sha256="ab" * 32, file_size=2048,
                          status=UploadStatus.COMMITTED, row_count=2, error_count=1, uploaded_by_id=boss.id,
                          committed_by_id=boss.id, committed_at=T2, created_at=T, period_start=date(2026, 10, 1),
                          period_end=date(2026, 10, 31),
                          summary={"rows": 2, "columns": {"employee": "Code", "date": "Date"}, "date_notes": [],
                                   "errors": [{"row": 3, "employee": "X9", "error": "unknown employee 'X9' (not in the app yet)"}]},
                          commit_summary={"applied_days": 1, "period_start": "2026-10-01"})
    preview = AttendanceUpload(filename="November 2026.xlsx", file_sha256="cd" * 32, file_size=10, created_at=T2,
                               status=UploadStatus.PREVIEW, row_count=0, error_count=0, uploaded_by_id=boss.id,
                               summary={"rows": 0})  # commit_summary never given: SQL NULL
    db.add(preview)
    db.add(up)
    db.flush()
    db.add_all([
        AttendanceUploadRow(upload_id=up.id, row_number=2, employee_ref="Intern - 90001", employee_id=emp.id,
                            work_date=date(2026, 10, 5), work_mode=WorkMode.OFFICE, is_badge=False, minutes=425,
                            first_in=T, last_out=T2),
        AttendanceUploadRow(upload_id=up.id, row_number=3, employee_ref="X9", work_date=date(2026, 10, 5),
                            raw_work_mode="wfo", is_badge=True, error="unknown employee 'X9' (not in the app yet)"),
        AttendanceDaily(employee_id=emp.id, work_date=date(2026, 10, 5), first_in=T, last_out=T2, office_minutes=425,
                        wfh_minutes=0, status=DailyStatus.PRESENT, corrected=False, computed_at=T2,
                        work_mode=WorkMode.OFFICE, wfo_source=WfoSource.UPLOAD_COLUMN,
                        conflicts=["UPLOAD_COLUMN said WFH; office signal from BADGE – checked"],
                        upload_work_mode=WorkMode.OFFICE, upload_minutes=425, upload_id=up.id, created_at=T,
                        updated_at=T2),
        AttendanceDaily(employee_id=emp.id, work_date=date(2026, 10, 6), office_minutes=0, wfh_minutes=0,
                        status=DailyStatus.ABSENT, corrected=False, conflicts=[], override_work_mode=WorkMode.LEAVE,
                        override_note="Approved by HR", overridden_by_id=boss.id, overridden_at=T2,
                        work_mode=WorkMode.LEAVE, wfo_source=WfoSource.MANUAL_OVERRIDE, created_at=T, updated_at=T),
        AuditLog(actor_user_id=boss.id, action="USER_UPDATED", entity_type="user", entity_id=str(emp.id),
                 before_values={"full_name": "Ravi K"}, after_values={"full_name": "Ravi Kumar", "note": "é – ok",
                                                                     "ratio": 66.7, "count": 3, "flag": True,
                                                                     "missing": None, "list": [1, "two"]},
                 request_id="req-1", ip_address="127.0.0.1", created_at=T),
        # audit.record() passes None explicitly: SQLAlchemy stores the JSON text 'null' (none_as_null=False).
        AuditLog(actor_user_id=boss.id, action="LOGIN", entity_type="user", entity_id=str(boss.id),
                 before_values=None, after_values={"method": "password"}, created_at=T2),
        UserSession(user_id=boss.id, token_hash="c" * 64, csrf_token="csrf-token", created_at=T, last_seen_at=T2,
                    expires_at=datetime(2026, 10, 5, 16, 52, 13, 123456, tzinfo=UTC), ip_address="127.0.0.1",
                    user_agent="pytest"),
        LoginAttempt(email="asha.rao@example.test", ip_address="127.0.0.1", succeeded=True, created_at=T),
    ])
    site = OfficeSite(name="Bengaluru One", code="BLR-1", network_name="Corp-WiFi", address="Fictional road 1",
                      is_active=True, created_at=T, updated_at=T)
    dev = Device(serial_number="SN12345", mac_address="aa:bb:cc:dd:ee:01", hostname="LAPTOP-01", model="Model X",
                 user_id=emp.id, is_active=True, source="IMPORT", created_at=T, updated_at=T)
    db.add_all([site, dev])
    db.flush()
    sess = AttendanceSession(employee_id=emp.id, check_in_at=T, check_out_at=T2, break_minutes=30, net_minutes=367,
                             status=SessionStatus.COMPLETED, source=SessionSource.WEB, work_location=WorkLocation.OFFICE,
                             created_at=T, updated_at=T)
    db.add(sess)
    db.flush()
    db.add_all([
        WifiEvent(employee_id=emp.id, ts_bucket=T, first_seen_at=T, last_seen_at=T2, event_type=WifiEventType.SEEN,
                  source="mock", access_point="AP-3", site_id=site.id, device_id=dev.id, created_at=T),
        PresenceEvent(employee_id=emp.id, ts_bucket=T, polled_at=T, availability="Available", activity="InAMeeting",
                      source="mock", created_at=T),
        AttendanceDailyCorrection(employee_id=emp.id, work_date=date(2026, 10, 6), requested_first_in=T,
                                  requested_last_out=T2, requested_status=DailyStatus.PRESENT, reason="Forgot badge",
                                  original_values={"status": "ABSENT"}, status=RequestStatus.PENDING, created_at=T,
                                  updated_at=T),
        AttendanceCorrection(employee_id=emp.id, attendance_date=date(2026, 10, 5), request_type=CorrectionType.MODIFY_SESSION,
                             original_session_id=sess.id, original_values={"net_minutes": 367},
                             requested_check_in_at=T, requested_check_out_at=T2, requested_break_minutes=0,
                             reason="Break was shorter", status=RequestStatus.APPROVED, reviewed_by_id=boss.id,
                             reviewed_at=T2, created_at=T, updated_at=T),
        LeaveRecord(employee_id=emp.id, start_date=date(2026, 10, 6), end_date=date(2026, 10, 7),
                    leave_type=LeaveType.ANNUAL, status=RequestStatus.APPROVED, reason="Trip", created_at=T,
                    updated_at=T),
    ])
    db.commit()
engine.dispose()
print("written", OUT, OUT.stat().st_size, "bytes")
