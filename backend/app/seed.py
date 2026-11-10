"""Repeatable demo data for local development (fictional company "Nexa Technologies").

* Users are upserted by email, so re-running never duplicates people.
* Attendance comes from monthly demo timesheets (CSV) imported through the real upload pipeline
  (preview + commit), one per month of the last ~75 days plus the current month up to yesterday. A month
  already imported is skipped, unless ``reset_demo`` is set (which removes the demo timesheets and the demo
  users' timesheet days, then imports them again relative to today).
* Passwords come from SEED_ADMIN_PASSWORD / SEED_DEMO_PASSWORD; nothing is hard-coded.
"""

import calendar
import csv
import hashlib
import io
import logging
import os
import random
import uuid
from dataclasses import dataclass
from datetime import date, time, timedelta

from sqlalchemy import delete, select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.config import get_settings
from app.core.context import RequestContext
from app.core.security import hash_password, validate_password_strength
from app.core.timeutil import local_date, office_tz
from app.models import (
    AttendanceDaily,
    AttendanceUpload,
    Department,
    Holiday,
    Role,
    UploadStatus,
    User,
    WorkSchedule,
)
from app.services import wfo_upload
from app.services.attendance_data import employee_data, schedule_data
from app.services.attendance_rules import resolve_schedule
from app.services.policy import get_policy

logger = logging.getLogger("ats.seed")
SEED_CTX = RequestContext(request_id="seed")
HISTORY_DAYS = 75
DOMAIN = "nexa.example"


class SeedError(RuntimeError):
    pass


DEPARTMENTS = {
    "AI Engineering": "Machine learning platforms and applied AI products.",
    "Software Engineering": "Core product engineering and platform services.",
    "Human Resources": "People operations, hiring and employee experience.",
    "Operations": "Facilities, IT operations and business operations.",
}


@dataclass
class Person:
    code: str
    name: str
    role: Role
    title: str
    department: str | None
    manager: str | None  # employee code
    # Demo history starts when people "joined", so earlier months are not counted as absences.
    joined_days_ago: int = HISTORY_DAYS
    tracked: bool = True


PEOPLE = [
    Person("ADM001", "System Administrator", Role.ADMIN, "System Administrator", None, None, 900, tracked=False),
    Person("MGR004", "Vikram Desai", Role.MANAGER, "Chief Operating Officer", "Operations", None),
    Person("MGR001", "Priya Sharma", Role.MANAGER, "AI Engineering Manager", "AI Engineering", "MGR004"),
    Person("MGR002", "Anil Kumar", Role.MANAGER, "Software Engineering Manager", "Software Engineering", "MGR004"),
    Person("MGR003", "Meera Nair", Role.MANAGER, "HR Manager", "Human Resources", None),
    Person("EMP001", "Rahul Reddy", Role.EMPLOYEE, "ML Engineer", "AI Engineering", "MGR001"),
    Person("EMP002", "Sneha Rao", Role.EMPLOYEE, "Data Scientist", "AI Engineering", "MGR001"),
    Person("EMP008", "Farah Khan", Role.EMPLOYEE, "Junior ML Engineer", "AI Engineering", "MGR001", 20),
    Person("EMP003", "Arjun Kumar", Role.EMPLOYEE, "Backend Engineer", "Software Engineering", "MGR002"),
    Person("EMP004", "Ananya Sharma", Role.EMPLOYEE, "Frontend Engineer", "Software Engineering", "MGR002"),
    Person("EMP007", "Rohan Gupta", Role.EMPLOYEE, "Site Reliability Engineer (night shift)",
           "Software Engineering", "MGR002"),
    Person("EMP006", "Divya Menon", Role.EMPLOYEE, "HR Generalist", "Human Resources", "MGR003"),
    Person("EMP005", "Kiran Patel", Role.EMPLOYEE, "Operations Analyst", "Operations", "MGR004"),
    Person("EMP009", "Neha Joshi", Role.EMPLOYEE, "Data Engineer", "AI Engineering", "MGR001"),
    Person("EMP010", "Vivek Singh", Role.EMPLOYEE, "QA Engineer", "Software Engineering", "MGR002"),
]


def demo_identities(code: str) -> tuple[str, str]:
    """Deterministic demo identities for automatic attendance: a device MAC (locally administered
    range 02:..., never a real vendor address) and an Entra object id. Used by the mock collectors."""
    n = int(hashlib.sha256(code.encode()).hexdigest()[:6], 16)
    mac = "02:a7:5e:" + ":".join(f"{(n >> shift) & 0xFF:02x}" for shift in (16, 8, 0))
    return mac, str(uuid.uuid5(uuid.NAMESPACE_URL, f"https://nexa.example/ats-demo/{code}"))
HEADS = {"AI Engineering": "MGR001", "Software Engineering": "MGR002", "Human Resources": "MGR003",
         "Operations": "MGR004"}


def _email(p: Person, admin_email: str) -> str:
    if p.role == Role.ADMIN:
        return admin_email
    first, last = p.name.lower().split(" ", 1)
    return f"{first}.{last.replace(' ', '')}@{DOMAIN}"


def _weekday_on_or_before(d: date) -> date:
    while d.isoweekday() > 5:
        d -= timedelta(days=1)
    return d


def _upsert_people(db: Session, admin_email: str, admin_pw: str, demo_pw: str, reset_passwords: bool,
                   today: date) -> dict[str, User]:
    depts: dict[str, Department] = {}
    for name, desc in DEPARTMENTS.items():
        d = db.scalar(select(Department).where(Department.name == name))
        if d is None:
            d = Department(name=name, description=desc)
            db.add(d)
        depts[name] = d
    db.flush()

    users: dict[str, User] = {}
    for p in PEOPLE:
        email = _email(p, admin_email)
        u = db.scalar(select(User).where(User.email == email)) or \
            db.scalar(select(User).where(User.employee_code == p.code))
        pw = admin_pw if p.role == Role.ADMIN else demo_pw
        if u is None:
            u = User(employee_code=p.code, email=email, password_hash=hash_password(pw), is_active=True,
                     full_name=p.name, role=p.role, date_joined=today - timedelta(days=p.joined_days_ago))
            db.add(u)
        elif reset_passwords:
            u.password_hash = hash_password(pw)
        u.full_name, u.role, u.job_title, u.attendance_tracked = p.name, p.role, p.title, p.tracked
        u.date_joined = today - timedelta(days=p.joined_days_ago)
        u.department_id = depts[p.department].id if p.department else None
        users[p.code] = u
    db.flush()
    for p in PEOPLE:
        users[p.code].manager_id = users[p.manager].id if p.manager else None
        if p.tracked:
            # Fill identities only where empty: never overwrite a real MAC or Entra id set by HR.
            mac, oid = demo_identities(p.code)
            u = users[p.code]
            if u.wifi_identity is None and db.scalar(select(User.id).where(User.wifi_identity == mac)) is None:
                u.wifi_identity = mac
            if u.graph_user_id is None and db.scalar(select(User.id).where(User.graph_user_id == oid)) is None:
                u.graph_user_id = oid
    for dept_name, code in HEADS.items():
        depts[dept_name].department_head_id = users[code].id
    db.flush()
    return users


def _ensure_schedules(db: Session, users: dict[str, User]) -> None:
    if db.scalar(select(WorkSchedule.id).limit(1)) is not None:
        return
    ops = db.scalar(select(Department).where(Department.name == "Operations"))
    db.add_all([
        WorkSchedule(name="General shift", start_time=time(9, 30), end_time=time(18, 30), required_minutes=480,
                     working_days=[1, 2, 3, 4, 5], effective_from=date(2024, 1, 1)),
        WorkSchedule(name="Operations shift (Mon-Sat)", department_id=ops.id, start_time=time(8, 0),
                     end_time=time(16, 30), required_minutes=450, working_days=[1, 2, 3, 4, 5, 6],
                     effective_from=date(2024, 1, 1)),
        WorkSchedule(name="Night shift", employee_id=users["EMP007"].id, start_time=time(21, 0),
                     end_time=time(6, 0), required_minutes=480, working_days=[1, 2, 3, 4, 5],
                     effective_from=date(2024, 1, 1)),
    ])
    db.flush()


def _ensure_holidays(db: Session, today: date) -> None:
    hr = db.scalar(select(Department).where(Department.name == "Human Resources"))
    wanted = [
        ("Founders' Day", _weekday_on_or_before(today - timedelta(days=17)), None),
        ("Regional Festival", _weekday_on_or_before(today - timedelta(days=46)), None),
        ("HR Annual Offsite", _weekday_on_or_before(today - timedelta(days=31)), hr.id),
        ("Company Foundation Day", today + timedelta(days=12), None),
    ]
    for name, d, dept_id in wanted:
        exists = db.scalar(select(Holiday.id).where(
            Holiday.holiday_date == d,
            Holiday.department_id.is_(None) if dept_id is None else Holiday.department_id == dept_id))
        if exists is None:
            db.add(Holiday(name=name, holiday_date=d, department_id=dept_id))
    db.flush()


DEMO_PREFIX = "demo_timesheet_"


def _months(today: date) -> list[tuple[date, date]]:
    """(first, last) day of each month to import: from HISTORY_DAYS ago up to yesterday."""
    first = (today - timedelta(days=HISTORY_DAYS)).replace(day=1)
    out = []
    m = first
    while m <= today - timedelta(days=1):
        last = min(m.replace(day=calendar.monthrange(m.year, m.month)[1]), today - timedelta(days=1))
        out.append((m, last))
        m = (m.replace(day=28) + timedelta(days=4)).replace(day=1)
    return out


def _timesheet_csv(db: Session, users: dict[str, User], start: date, end: date) -> bytes:
    """One month's demo timesheet: Employee ID, Name, Date, Work Mode, In, Out. Most people are in the office
    Monday to Wednesday and work from home on Thursday and Friday (Kiran, EMP005, is in only on Tuesdays);
    about 4% of working days are missing (absent), 2% are leave, and a few Saturdays are worked."""
    schedules = [schedule_data(x) for x in db.scalars(select(WorkSchedule))]
    holidays = {(h.holiday_date, h.department_id) for h in db.scalars(select(Holiday))}
    buf = io.StringIO()
    w = csv.writer(buf, lineterminator="\n")
    w.writerow(["Employee ID", "Employee", "Date", "Work Mode", "In", "Out"])
    for code, u in users.items():
        if not u.attendance_tracked:
            continue
        rng = random.Random(f"nexa-{code}-{start:%Y-%m}")
        emp = employee_data(u)
        office_days = {1} if code == "EMP005" else {0, 1, 2}
        d = max(start, u.date_joined)
        while d <= end:
            sched = resolve_schedule(schedules, emp, d)
            day, d = d, d + timedelta(days=1)
            if sched is None:
                continue
            holiday = (day, None) in holidays or (day, u.department_id) in holidays
            workday = day.isoweekday() in sched.working_days and not holiday
            if not workday and not (day.isoweekday() == 6 and not holiday and rng.random() < 0.06):
                continue
            roll = rng.random()
            if workday and roll < 0.04:
                continue  # absent: not in the timesheet
            if workday and roll < 0.06:
                w.writerow([u.employee_code, u.full_name, day.strftime("%d/%m/%Y"), "Leave", "", ""])
                continue
            mode = "WFO" if (day.weekday() in office_days or not workday) else "WFH"
            start_min = sched.start_time.hour * 60 + sched.start_time.minute + int(rng.gauss(4, 14))
            end_min = start_min + sched.required_minutes + int(rng.gauss(20, 25)) + rng.choice([30, 45, 60])
            t_in = f"{start_min // 60 % 24:02d}:{start_min % 60:02d}"
            t_out = f"{end_min // 60 % 24:02d}:{end_min % 60:02d}"
            w.writerow([u.employee_code, u.full_name, day.strftime("%d/%m/%Y"), mode, t_in, t_out])
    return buf.getvalue().encode("utf-8")


def _import_timesheets(db: Session, users: dict[str, User], admin: User, today: date) -> int:
    """Import each month's demo timesheet through the real pipeline (preview, then commit)."""
    imported = 0
    for start, end in _months(today):
        name = f"{DEMO_PREFIX}{start:%Y-%m}.csv"
        if db.scalar(select(AttendanceUpload.id).where(AttendanceUpload.filename == name,
                                                       AttendanceUpload.status == UploadStatus.COMMITTED)):
            continue
        upload = wfo_upload.preview(db, admin, name, _timesheet_csv(db, users, start, end), SEED_CTX)
        wfo_upload.commit(db, admin, upload.id, overwrite_overrides=False, ctx=SEED_CTX)
        imported += 1
    return imported


def _reset_demo(db: Session, users: dict[str, User]) -> None:
    """Remove the demo timesheets and the demo users' timesheet days (only with --reset-demo)."""
    ids = [u.id for u in users.values()]
    db.execute(delete(AttendanceDaily).where(AttendanceDaily.employee_id.in_(ids)))
    db.execute(delete(AttendanceUpload).where(AttendanceUpload.filename.like(f"{DEMO_PREFIX}%")))
    db.flush()


def run(db: Session, *, reset_demo: bool = False, reset_passwords: bool = False) -> dict:
    settings = get_settings()
    if settings.app_env == "production":
        raise SeedError("Refusing to load demo data when APP_ENV=production. Use `python -m app.cli create-admin`.")
    admin_email = (os.environ.get("SEED_ADMIN_EMAIL") or "").strip().lower()
    admin_pw = os.environ.get("SEED_ADMIN_PASSWORD") or ""
    demo_pw = os.environ.get("SEED_DEMO_PASSWORD") or ""
    if not admin_email or not admin_pw or not demo_pw:
        raise SeedError("Set SEED_ADMIN_EMAIL, SEED_ADMIN_PASSWORD and SEED_DEMO_PASSWORD (see backend/.env.example)")
    for pw in (admin_pw, demo_pw):
        validate_password_strength(pw)

    now = clock.utcnow()
    today = local_date(now, office_tz())
    get_policy(db)
    users = _upsert_people(db, admin_email, admin_pw, demo_pw, reset_passwords, today)
    if reset_demo:
        _reset_demo(db, users)
    _ensure_schedules(db, users)
    _ensure_holidays(db, today)
    db.commit()
    admin = db.scalar(select(User).where(User.email == admin_email))
    imported = _import_timesheets(db, users, admin, today)
    return {"users": len(users), "timesheets_imported": imported, "today": today.isoformat()}
