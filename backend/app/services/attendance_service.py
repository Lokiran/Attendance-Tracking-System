"""Check-in / check-out and per-employee attendance queries."""

import logging
from datetime import date, timedelta

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.core import clock
from app.core.context import RequestContext
from app.core.errors import Conflict, Forbidden, ValidationFailed
from app.core.timeutil import local_date, local_day_bounds, office_tz
from app.models import (
    AttendanceCorrection,
    AttendanceSession,
    RequestStatus,
    SessionSource,
    SessionStatus,
    User,
    WorkSchedule,
)
from app.services import audit
from app.services.attendance_data import compute_records, employee_data, schedule_data, session_data
from app.services.attendance_rules import (
    DayRecord,
    InvalidSessionTimes,
    Summary,
    is_unresolved,
    resolve_schedule,
    session_net_minutes,
    summarize,
)
from app.services.policy import get_policy, policy_data

logger = logging.getLogger("ats.attendance")


def _lock_employee(db: Session, employee_id: int) -> User:
    """Serialise attendance writes per employee (SELECT ... FOR UPDATE on PostgreSQL)."""
    # Lock by id only: FOR UPDATE cannot apply to the eager outer joins configured on User.
    db.execute(select(User.id).where(User.id == employee_id).with_for_update()).scalar_one()
    return db.get(User, employee_id, populate_existing=True)


def _open_session(db: Session, employee_id: int) -> AttendanceSession | None:
    return db.scalar(
        select(AttendanceSession).where(
            AttendanceSession.employee_id == employee_id, AttendanceSession.status == SessionStatus.OPEN
        )
    )


def _mark_missing_checkout(db: Session, s: AttendanceSession, ctx: RequestContext | None) -> None:
    s.status = SessionStatus.MISSING_CHECKOUT
    audit.record(db, actor_id=None, action="SESSION_MARKED_MISSING_CHECKOUT", entity_type="attendance_session",
                 entity_id=s.id, after={"check_in_at": s.check_in_at, "employee_id": s.employee_id}, ctx=ctx)


def check_in(db: Session, user: User, ctx: RequestContext) -> AttendanceSession:
    employee = _lock_employee(db, user.id)
    if not employee.is_active:
        raise Forbidden("Inactive accounts cannot record attendance")
    now = clock.utcnow()
    tz = office_tz()
    policy = policy_data(get_policy(db))

    existing = _open_session(db, employee.id)
    if existing is not None:
        if is_unresolved(session_data(existing), local_date(now, tz), now, tz, policy):
            # A forgotten session from an earlier day stays on record as unresolved; a new day can start.
            _mark_missing_checkout(db, existing, ctx)
            db.flush()
        else:
            raise Conflict("You are already checked in", code="already_checked_in")

    clash = db.scalar(
        select(AttendanceSession.id).where(
            AttendanceSession.employee_id == employee.id,
            AttendanceSession.status == SessionStatus.COMPLETED,
            AttendanceSession.check_out_at > now,
        )
    )
    if clash is not None:
        raise Conflict("A recorded session already covers the current time", code="session_overlap")

    session = AttendanceSession(employee_id=employee.id, check_in_at=now, status=SessionStatus.OPEN,
                                source=SessionSource.WEB, break_minutes=0)
    db.add(session)
    try:
        db.commit()
    except IntegrityError:
        db.rollback()
        raise Conflict("You are already checked in", code="already_checked_in") from None
    logger.info("check-in employee=%s session=%s", employee.id, session.id)
    return session


def check_out(db: Session, user: User, break_minutes: int, ctx: RequestContext) -> AttendanceSession:
    employee = _lock_employee(db, user.id)
    now = clock.utcnow()
    tz = office_tz()
    policy = policy_data(get_policy(db))
    session = _open_session(db, employee.id)
    if session is None:
        raise Conflict("You are not checked in", code="not_checked_in")
    if is_unresolved(session_data(session), local_date(now, tz), now, tz, policy):
        _mark_missing_checkout(db, session, ctx)
        db.commit()
        raise Conflict(
            "This session exceeded the maximum session length. "
            "It has been flagged as a missing check-out; please submit a correction request.",
            code="session_expired",
        )
    try:
        net = session_net_minutes(session.check_in_at, now, break_minutes)
    except InvalidSessionTimes as exc:
        raise ValidationFailed(str(exc)) from None
    # Closing the session turns it into the range [check_in, now). Check it here (SQLite has no
    # exclusion constraint); on PostgreSQL ex_attendance_no_overlap is the backstop.
    clash = db.scalar(
        select(AttendanceSession.id).where(
            AttendanceSession.employee_id == employee.id,
            AttendanceSession.id != session.id,
            AttendanceSession.status == SessionStatus.COMPLETED,
            AttendanceSession.check_in_at < now,
            AttendanceSession.check_out_at > session.check_in_at,
        ).limit(1)
    )
    if clash is not None:
        raise Conflict("This session overlaps another recorded session. Ask your manager to review it.",
                       code="session_overlap")
    session.check_out_at = now
    session.break_minutes = break_minutes
    session.net_minutes = net
    session.status = SessionStatus.COMPLETED
    try:
        db.commit()
    except IntegrityError:
        db.rollback()
        raise Conflict("This session overlaps another recorded session. Ask your manager to review it.",
                       code="session_overlap") from None
    logger.info("check-out employee=%s session=%s net=%s", employee.id, session.id, net)
    return session


def records_for(db: Session, employee: User, start: date, end: date) -> list[DayRecord]:
    return compute_records(db, [employee], start, end)[employee.id]


def summary_for(db: Session, employee: User, start: date, end: date) -> tuple[Summary, list[DayRecord]]:
    records = records_for(db, employee, start, end)
    return summarize(records, start, end), records


def current_status(db: Session, employee: User) -> dict:
    now = clock.utcnow()
    tz = office_tz()
    today = local_date(now, tz)
    policy = get_policy(db)
    open_s = _open_session(db, employee.id)
    active = unresolved = None
    if open_s is not None:
        if is_unresolved(session_data(open_s), today, now, tz, policy_data(policy)):
            unresolved = open_s
        else:
            active = open_s
    if unresolved is None:
        # Only surface what the employee can still act on: inside the correction window and
        # without a correction already awaiting review.
        window_start = local_day_bounds(today - timedelta(days=policy.correction_window_days))[0]
        pending = select(AttendanceCorrection.original_session_id).where(
            AttendanceCorrection.status == RequestStatus.PENDING,
            AttendanceCorrection.original_session_id.is_not(None))
        unresolved = db.scalar(
            select(AttendanceSession)
            .where(AttendanceSession.employee_id == employee.id,
                   AttendanceSession.status == SessionStatus.MISSING_CHECKOUT,
                   AttendanceSession.check_in_at >= window_start,
                   AttendanceSession.id.not_in(pending))
            .order_by(AttendanceSession.check_in_at.desc())
            .limit(1)
        )
    records = records_for(db, employee, today, today)
    schedules = [schedule_data(s) for s in db.scalars(select(WorkSchedule))]
    schedule = resolve_schedule(schedules, employee_data(employee), today)
    return {
        "server_time": now,
        "today": today,
        "office_timezone": tz.key,
        "today_record": records[0] if records else None,
        "active_session": active,
        "unresolved_session": unresolved,
        "schedule": schedule,
        "max_session_minutes": policy.max_session_minutes,
    }


def sessions_on(db: Session, employee_id: int, day: date) -> list[AttendanceSession]:
    lo, hi = local_day_bounds(day)
    return list(
        db.scalars(
            select(AttendanceSession)
            .where(AttendanceSession.employee_id == employee_id, AttendanceSession.check_in_at >= lo,
                   AttendanceSession.check_in_at < hi)
            .order_by(AttendanceSession.check_in_at)
        )
    )
