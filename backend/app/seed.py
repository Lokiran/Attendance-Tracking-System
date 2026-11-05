"""Repeatable demo data for local development (fictional company "Nexa Technologies").

* Users are upserted by email, so re-running never duplicates people.
* Attendance, leave and corrections are generated only for demo users that have no attendance
  yet, unless ``reset_demo`` is set (which regenerates them relative to today).
* Passwords come from SEED_ADMIN_PASSWORD / SEED_DEMO_PASSWORD; nothing is hard-coded.
"""

import logging
import os
import random
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta

from sqlalchemy import delete, select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.config import get_settings
from app.core.context import RequestContext
from app.core.security import hash_password, validate_password_strength
from app.core.timeutil import local_date, local_to_utc, office_tz
from app.models import (
    AttendanceCorrection,
    AttendanceSession,
    Department,
    Holiday,
    LeaveRecord,
    LeaveType,
    RequestStatus,
    Role,
    SessionSource,
    SessionStatus,
    User,
    WorkSchedule,
)
from app.services import corrections
from app.services.attendance_data import employee_data, schedule_data
from app.services.attendance_rules import resolve_schedule, session_net_minutes
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
]
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


# code -> [(days_ago_start, length, type, status)]; a negative days_ago is in the future.
LEAVE_PLAN: dict[str, list[tuple[int, int, LeaveType, RequestStatus]]] = {
        "EMP001": [(40, 2, LeaveType.ANNUAL, RequestStatus.APPROVED)],
        "EMP002": [(22, 1, LeaveType.SICK, RequestStatus.APPROVED), (-9, 3, LeaveType.ANNUAL, RequestStatus.PENDING)],
        "EMP003": [(12, 3, LeaveType.ANNUAL, RequestStatus.APPROVED), (5, 1, LeaveType.CASUAL, RequestStatus.REJECTED)],
        "EMP004": [(0, 1, LeaveType.CASUAL, RequestStatus.APPROVED)],
        "EMP005": [(55, 4, LeaveType.ANNUAL, RequestStatus.APPROVED)],
        "EMP006": [(8, 2, LeaveType.SICK, RequestStatus.APPROVED), (-20, 2, LeaveType.ANNUAL, RequestStatus.PENDING)],
        "EMP007": [(30, 2, LeaveType.ANNUAL, RequestStatus.APPROVED)],
        "MGR001": [(26, 1, LeaveType.CASUAL, RequestStatus.APPROVED)],
        "MGR002": [(60, 2, LeaveType.ANNUAL, RequestStatus.APPROVED)],
}


def _generate_attendance(db: Session, users: dict[str, User], reviewer_for: dict[int, User], today: date,
                         now: datetime) -> int:
    tz = office_tz()
    schedules = [schedule_data(s) for s in db.scalars(select(WorkSchedule))]
    all_holidays = {(h.holiday_date, h.department_id) for h in db.scalars(select(Holiday))}
    created = 0
    for code, u in users.items():
        if not u.attendance_tracked:
            continue
        if db.scalar(select(AttendanceSession.id).where(AttendanceSession.employee_id == u.id).limit(1)):
            continue  # already has attendance: keep it
        rng = random.Random(f"nexa-{code}")
        leave_days: set[date] = set()
        for ago, length, ltype, status in LEAVE_PLAN.get(code, []):
            start = today - timedelta(days=ago)
            end = start + timedelta(days=length - 1)
            reviewer = reviewer_for.get(u.id)
            db.add(LeaveRecord(
                employee_id=u.id, start_date=start, end_date=end, leave_type=ltype, status=status,
                reason={LeaveType.SICK: "Unwell", LeaveType.ANNUAL: "Family vacation",
                        LeaveType.CASUAL: "Personal errand"}.get(ltype, "Personal"),
                reviewed_by_id=reviewer.id if reviewer and status != RequestStatus.PENDING else None,
                reviewed_at=now - timedelta(days=max(ago, 0) + 2) if status != RequestStatus.PENDING else None,
                reviewer_comment="Clashes with release week" if status == RequestStatus.REJECTED else None,
            ))
            if status == RequestStatus.APPROVED:
                leave_days.update(start + timedelta(days=i) for i in range(length))

        emp = employee_data(u)
        start_day = max(today - timedelta(days=HISTORY_DAYS), u.date_joined)
        d = start_day
        while d <= today:
            sched = resolve_schedule(schedules, emp, d)
            is_holiday = (d, None) in all_holidays or (d, u.department_id) in all_holidays
            workday = sched is not None and d.isoweekday() in sched.working_days and not is_holiday
            if d in leave_days or sched is None:
                d += timedelta(days=1)
                continue
            if not workday:
                # Occasional weekend work shows up as extra days present.
                if not (d.isoweekday() == 6 and rng.random() < 0.06 and not is_holiday):
                    d += timedelta(days=1)
                    continue
            if d < today and rng.random() < 0.045:
                d += timedelta(days=1)
                continue  # unexplained absence

            start_offset = int(rng.gauss(4, 14))
            check_in = local_to_utc(d, sched.start_time, tz) + timedelta(minutes=start_offset)
            work = sched.required_minutes + int(rng.gauss(20, 25))
            if check_in >= now:
                d += timedelta(days=1)
                continue  # not checked in yet today
            missing = d < today and rng.random() < 0.03
            overnight = sched.end_time < sched.start_time
            split = not missing and not overnight and rng.random() < 0.18
            if d == today:
                # Today: leave the day's (only) session open if it would still be running.
                end = check_in + timedelta(minutes=work + 45)
                if end > now:
                    if rng.random() < 0.85:
                        db.add(AttendanceSession(employee_id=u.id, check_in_at=check_in, status=SessionStatus.OPEN,
                                                 source=SessionSource.SEED, break_minutes=0))
                        created += 1
                    d += timedelta(days=1)
                    continue
            if missing:
                db.add(AttendanceSession(employee_id=u.id, check_in_at=check_in,
                                         status=SessionStatus.MISSING_CHECKOUT, source=SessionSource.SEED,
                                         break_minutes=0))
                created += 1
            elif split:
                first_len = rng.randint(180, 240)
                gap = rng.randint(35, 60)
                first_out = check_in + timedelta(minutes=first_len)
                second_in = first_out + timedelta(minutes=gap)
                second_out = second_in + timedelta(minutes=max(work - first_len, 60))
                for a, b in ((check_in, first_out), (second_in, second_out)):
                    if b <= now:
                        db.add(AttendanceSession(employee_id=u.id, check_in_at=a, check_out_at=b, break_minutes=0,
                                                 net_minutes=session_net_minutes(a, b, 0),
                                                 status=SessionStatus.COMPLETED, source=SessionSource.SEED))
                        created += 1
            else:
                brk = rng.choice([30, 45, 45, 60])
                check_out = check_in + timedelta(minutes=work + brk)
                if check_out <= now:
                    db.add(AttendanceSession(employee_id=u.id, check_in_at=check_in, check_out_at=check_out,
                                             break_minutes=brk, net_minutes=session_net_minutes(check_in, check_out, brk),
                                             status=SessionStatus.COMPLETED, source=SessionSource.SEED))
                else:  # overnight shift that started yesterday and is still running
                    db.add(AttendanceSession(employee_id=u.id, check_in_at=check_in, status=SessionStatus.OPEN,
                                             source=SessionSource.SEED, break_minutes=0))
                created += 1
            d += timedelta(days=1)
        db.flush()
    return created


def _demo_corrections(db: Session, users: dict[str, User], today: date) -> None:
    """Pending, approved and rejected corrections created through the real workflow."""
    if db.scalar(select(AttendanceCorrection.id).limit(1)) is not None:
        return
    tz = office_tz()
    priya, anil = users["MGR001"], users["MGR002"]

    def missing_session(emp: User):
        return db.scalar(select(AttendanceSession).where(
            AttendanceSession.employee_id == emp.id, AttendanceSession.status == SessionStatus.MISSING_CHECKOUT,
            AttendanceSession.check_in_at >= local_to_utc(today - timedelta(days=25), time(0), tz))
            .order_by(AttendanceSession.check_in_at.desc()))

    def single_session_day(emp: User) -> AttendanceSession | None:
        """A recent completed session that is the only one on its day (safe to re-time)."""
        candidates = db.scalars(select(AttendanceSession).where(
            AttendanceSession.employee_id == emp.id, AttendanceSession.status == SessionStatus.COMPLETED,
            AttendanceSession.check_in_at >= local_to_utc(today - timedelta(days=25), time(0), tz),
            AttendanceSession.check_in_at < local_to_utc(today - timedelta(days=2), time(0), tz))
            .order_by(AttendanceSession.check_in_at.desc())).all()
        days = [local_date(c.check_in_at, tz) for c in candidates]
        return next((c for c, d in zip(candidates, days) if days.count(d) == 1), None)

    def absent_weekday(emp: User, days_back: int) -> date | None:
        for i in range(days_back, days_back + 20):
            d = today - timedelta(days=i)
            if d.isoweekday() > 5:
                continue
            lo = local_to_utc(d, time(0), tz)
            hi = local_to_utc(d + timedelta(days=1), time(0), tz)
            if db.scalar(select(AttendanceSession.id).where(
                    AttendanceSession.employee_id == emp.id, AttendanceSession.check_in_at >= lo,
                    AttendanceSession.check_in_at < hi)) is None and \
                    db.scalar(select(LeaveRecord.id).where(LeaveRecord.employee_id == emp.id,
                                                           LeaveRecord.start_date <= d,
                                                           LeaveRecord.end_date >= d)) is None:
                return d
        return None

    for emp, reviewer, outcome in ((users["EMP001"], priya, "pending"), (users["EMP002"], priya, "approve"),
                                   (users["EMP003"], anil, "reject"), (users["EMP004"], anil, "approve")):
        s = missing_session(emp)
        if s is not None:
            day = local_date(s.check_in_at, tz)
            c = corrections.submit(
                db, emp, attendance_date=day, check_in_time=s.check_in_at.astimezone(tz).time().replace(microsecond=0),
                check_out_time=time(18, 15), check_out_next_day=False, break_minutes=45,
                reason="Forgot to check out before leaving the office", session_id=s.id, ctx=SEED_CTX)
        elif (day := absent_weekday(emp, 3)) is None:
            s = single_session_day(emp)
            if s is None:
                continue
            local_in = s.check_in_at.astimezone(tz)
            c = corrections.submit(
                db, emp, attendance_date=local_in.date(), check_in_time=local_in.time().replace(microsecond=0),
                check_out_time=time(20, 30), check_out_next_day=False, break_minutes=s.break_minutes,
                reason="Stayed late for a production release; check-out was recorded too early", session_id=s.id,
                ctx=SEED_CTX)
        else:
            c = corrections.submit(
                db, emp, attendance_date=day, check_in_time=time(9, 35), check_out_time=time(18, 20),
                check_out_next_day=False, break_minutes=45,
                reason="Badge reader outage - attended in person but could not check in", session_id=None,
                ctx=SEED_CTX)
        if outcome == "approve":
            corrections.approve(db, reviewer, c.id, "Verified with the team calendar", SEED_CTX)
        elif outcome == "reject":
            corrections.reject(db, reviewer, c.id, "No record of attendance on this date", SEED_CTX)


def _reset_demo(db: Session, users: dict[str, User]) -> None:
    ids = [u.id for u in users.values()]
    db.execute(delete(AttendanceCorrection).where(AttendanceCorrection.employee_id.in_(ids)))
    db.execute(delete(AttendanceSession).where(AttendanceSession.employee_id.in_(ids)))
    db.execute(delete(LeaveRecord).where(LeaveRecord.employee_id.in_(ids)))
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
    reviewer_for = {u.id: db.get(User, u.manager_id) for u in users.values() if u.manager_id}
    created = _generate_attendance(db, users, reviewer_for, today, now)
    db.commit()
    _demo_corrections(db, users, today)
    db.commit()
    return {"users": len(users), "sessions_created": created, "today": today.isoformat()}
