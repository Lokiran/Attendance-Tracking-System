"""Scale benchmark: 1 admin, a few managers and many employees, each with ~100 days of attendance.

Builds a throw-away SQLite database in the system temp folder (it never touches your dev or
production database), then times the dashboard API the way the browser loads each page.

Usage (PowerShell, from the repository root):
    backend\\.venv\\Scripts\\python.exe scripts\\scale_benchmark.py --employees 5000
    backend\\.venv\\Scripts\\python.exe scripts\\scale_benchmark.py --employees 10000 --managers 8

Results from a development laptop are recorded in docs/scaling.md.
"""

import argparse
import os
import random
import secrets
import subprocess
import sys
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import date, time as dtime, timedelta
from pathlib import Path

BACKEND = Path(__file__).resolve().parents[1] / "backend"
DB_FILE = Path(tempfile.gettempdir()) / "ats-scale-benchmark.sqlite3"
HISTORY_DAYS = 100


def configure_env() -> None:
    os.environ["ATS_ENV_FILE"] = os.environ.get("ATS_ENV_FILE", ".env.sqlite")
    os.environ["DATABASE_URL"] = "sqlite:///" + DB_FILE.as_posix()
    os.environ["APP_ENV"] = "development"
    sys.path.insert(0, str(BACKEND))


def build(employees: int, managers: int, password: str) -> None:
    for suffix in ("", "-wal", "-shm"):
        Path(str(DB_FILE) + suffix).unlink(missing_ok=True)
    subprocess.run([sys.executable, "-m", "alembic", "upgrade", "head"], cwd=BACKEND, env=os.environ, check=True,
                   capture_output=True)
    from sqlalchemy import func, insert, select

    from app.core.security import hash_password
    from app.core.timeutil import local_to_utc, office_tz
    from app.db.session import SessionLocal
    from app.models import (AttendanceDaily, AttendanceUpload, AttendanceUploadRow, DailyStatus, Department, Role,
                            UploadStatus, User, WorkMode, WorkSchedule)
    from app.services.policy import get_policy

    tz, rng, today = office_tz(), random.Random(7), date.today()
    pw = hash_password(password)
    with SessionLocal() as db:
        get_policy(db)
        depts = [Department(name=f"Dept {i}") for i in range(6)]
        db.add_all(depts)
        db.add(WorkSchedule(name="General", start_time=dtime(9, 30), end_time=dtime(18, 30), required_minutes=480,
                            working_days=[1, 2, 3, 4, 5], effective_from=date(2024, 1, 1)))
        admin = User(employee_code="ADM001", full_name="Admin", email="admin@scale.example", password_hash=pw,
                     role=Role.ADMIN, attendance_tracked=False, date_joined=date(2024, 1, 1))
        db.add(admin)
        db.flush()
        mgrs = [User(employee_code=f"MGR{m:03d}", full_name=f"Manager {m:02d}", email=f"mgr{m}@scale.example",
                     password_hash=pw, role=Role.MANAGER, department_id=depts[m % 6].id, manager_id=admin.id,
                     date_joined=date(2024, 1, 1)) for m in range(managers)]
        db.add_all(mgrs)
        db.flush()
        db.execute(insert(User), [
            dict(employee_code=f"EMP{i:05d}", full_name=f"Employee {i:05d}", email=f"emp{i}@scale.example",
                 password_hash=pw, role=Role.EMPLOYEE, department_id=depts[i % 6].id, manager_id=mgrs[i % managers].id,
                 is_active=True, attendance_tracked=True, date_joined=date(2024, 1, 1))
            for i in range(employees)])
        db.commit()
        ids = list(db.scalars(select(User.id).where(User.attendance_tracked.is_(True))))
        # One committed timesheet covering the whole history for everyone (as monthly imports would).
        first = today - timedelta(days=HISTORY_DAYS)
        upload = AttendanceUpload(filename="benchmark.csv", file_sha256="0" * 64, file_size=1,
                                  status=UploadStatus.COMMITTED, row_count=len(ids), error_count=0,
                                  period_start=first, period_end=today - timedelta(days=1))
        db.add(upload)
        db.flush()
        db.execute(insert(AttendanceUploadRow), [dict(upload_id=upload.id, row_number=i + 2, employee_ref=str(uid),
                                                      employee_id=uid, work_date=first, is_badge=False)
                                                 for i, uid in enumerate(ids)])
        batch: list[dict] = []
        for d in (today - timedelta(days=k) for k in range(HISTORY_DAYS, 0, -1)):
            if d.isoweekday() > 5:
                continue
            for uid in ids:
                if rng.random() < 0.06:
                    continue  # absent: not in the timesheet
                a = local_to_utc(d, dtime(9, 30), tz) + timedelta(minutes=rng.randint(-20, 40))
                wfh = rng.random() < 0.3
                batch.append(dict(employee_id=uid, work_date=d, office_minutes=0, wfh_minutes=0, corrected=False,
                                  conflicts=[], status=DailyStatus.PRESENT, upload_id=upload.id,
                                  upload_work_mode=WorkMode.WFH if wfh else WorkMode.OFFICE,
                                  upload_minutes=rng.randint(435, 515), first_in=None if wfh else a))
                if len(batch) >= 20000:
                    db.execute(insert(AttendanceDaily), batch)
                    batch.clear()
        if batch:
            db.execute(insert(AttendanceDaily), batch)
        db.commit()
        print(f"built {DB_FILE}: {db.scalar(select(func.count()).select_from(User))} users, "
              f"{db.scalar(select(func.count()).select_from(AttendanceDaily))} timesheet days")


def page_calls(rng: dict, admin_page: bool) -> list[tuple[str, dict]]:
    """The API calls the dashboard page makes, with the same parameters as the frontend."""
    on = dict(rng, on_date=rng["end"])
    calls = [("/dashboard/overview", on), ("/dashboard/employees", dict(on, page_size=10)),
             ("/dashboard/employees", dict(on, page_size=12, sort="attendance_percentage", order="asc")),
             ("/dashboard/employees", dict(on, page_size=12, sort="average_net_minutes", order="asc")),
             ("/dashboard/trends", dict(rng, granularity="day")), ("/dashboard/trends", dict(rng, granularity="week")),
             ("/dashboard/exceptions", dict(rng, page_size=8))]
    if admin_page:
        calls += [("/dashboard/departments", on), ("/dashboard/managers", on)]
    return calls


def run(password: str) -> None:
    from fastapi.testclient import TestClient

    from app.main import app
    from app.services import record_cache

    def signed_in(email: str) -> TestClient:
        c = TestClient(app)
        r = c.post("/api/auth/login", json={"email": email, "password": password})
        r.raise_for_status()
        return c

    today = date.today()
    prev_end = today.replace(day=1) - timedelta(days=1)
    ranges = {"this month": {"start": today.replace(day=1).isoformat(), "end": today.isoformat()},
              "last month": {"start": prev_end.replace(day=1).isoformat(), "end": prev_end.isoformat()},
              "90 days": {"start": (today - timedelta(days=90)).isoformat(), "end": today.isoformat()}}
    people = {"admin (whole org)": (signed_in("admin@scale.example"), True),
              "manager (one team)": (signed_in("mgr0@scale.example"), False)}
    print("\nPage ready (all calls in parallel like a browser, nothing cached):")
    for who, (client, is_admin) in people.items():
        for name, rng in ranges.items():
            record_cache.clear()

            def one(call, client=client):
                client.get("/api" + call[0], params=call[1]).raise_for_status()

            t0 = time.perf_counter()
            with ThreadPoolExecutor(6) as ex:
                list(ex.map(one, page_calls(rng, is_admin)))
            print(f"  {who:20s} {name:11s} {(time.perf_counter() - t0) * 1000:7.0f} ms")

    emp = signed_in("emp1@scale.example")
    print("\nSingle requests:")
    for label, client, path, params in (
        ("employee: today panel", emp, "/attendance/current", {}),
        ("admin: people search", people["admin (whole org)"][0], "/team/members", {"search": "Employee 0123", "limit": 20}),
        ("admin: user list page", people["admin (whole org)"][0], "/admin/users", {"page_size": 20}),
        ("admin: team CSV (month)", people["admin (whole org)"][0], "/reports/team-attendance.csv", ranges["this month"]),
    ):
        t0 = time.perf_counter()
        client.get("/api" + path, params=params).raise_for_status()
        print(f"  {label:32s} {(time.perf_counter() - t0) * 1000:7.0f} ms")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--employees", type=int, default=5000)
    ap.add_argument("--managers", type=int, default=8)
    ap.add_argument("--reuse", action="store_true", help="time the database from the previous run without rebuilding")
    args = ap.parse_args()
    configure_env()
    pw_file = DB_FILE.with_suffix(".password")
    if args.reuse and DB_FILE.exists() and pw_file.exists():
        password = pw_file.read_text().strip()
    else:
        password = secrets.token_urlsafe(18) + "!aA1"  # throw-away accounts in a throw-away database
        build(args.employees, args.managers, password)
        pw_file.write_text(password)
    run(password)
    return 0


if __name__ == "__main__":
    sys.exit(main())
