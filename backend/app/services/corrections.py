"""Attendance correction workflow.

Employees never edit sessions directly. An approved correction VOIDS the original session
(kept for history, with a void reason) and creates a new COMPLETED session sourced CORRECTION.
"""

from datetime import date, datetime, time, timedelta

from sqlalchemy import and_, or_, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.core import clock
from app.core.context import RequestContext
from app.core.errors import Conflict, Forbidden, NotFound, ValidationFailed
from app.core.timeutil import local_date, local_to_utc, office_tz
from app.models import (
    AttendanceCorrection,
    AttendanceSession,
    CorrectionType,
    RequestStatus,
    Role,
    SessionSource,
    SessionStatus,
    User,
)
from app.services import audit, wfo
from app.services.attendance_data import session_data
from app.services.attendance_rules import InvalidSessionTimes, is_unresolved, session_net_minutes
from app.services.policy import get_policy, policy_data
from app.services.scope import can_view_employee, subtree_ids


def session_snapshot(s: AttendanceSession) -> dict:
    return {
        "session_id": s.id, "check_in_at": s.check_in_at.isoformat(),
        "check_out_at": s.check_out_at.isoformat() if s.check_out_at else None,
        "break_minutes": s.break_minutes, "net_minutes": s.net_minutes, "status": s.status.value,
    }


def correction_snapshot(c: AttendanceCorrection) -> dict:
    return {
        "employee_id": c.employee_id, "attendance_date": c.attendance_date, "request_type": c.request_type,
        "original_session_id": c.original_session_id, "requested_check_in_at": c.requested_check_in_at,
        "requested_check_out_at": c.requested_check_out_at, "requested_break_minutes": c.requested_break_minutes,
        "status": c.status, "reviewer_comment": c.reviewer_comment, "resulting_session_id": c.resulting_session_id,
    }


def _overlaps(db: Session, employee_id: int, start: datetime, end: datetime, exclude_id: int | None) -> bool:
    """True if [start, end) collides with another non-voided session of the employee.

    * COMPLETED sessions occupy [check_in, check_out).
    * OPEN sessions are still running, so they occupy [check_in, ∞); otherwise a correction could
      be placed inside them and the later check-out would violate the PostgreSQL exclusion
      constraint (and silently overlap on other databases).
    * MISSING_CHECKOUT sessions have no known end; only their start instant is reserved.
    """
    completed = and_(AttendanceSession.status == SessionStatus.COMPLETED,
                     AttendanceSession.check_in_at < end, AttendanceSession.check_out_at > start)
    running = and_(AttendanceSession.status == SessionStatus.OPEN, AttendanceSession.check_in_at < end)
    missing = and_(AttendanceSession.status == SessionStatus.MISSING_CHECKOUT,
                   AttendanceSession.check_in_at >= start, AttendanceSession.check_in_at < end)
    stmt = select(AttendanceSession.id).where(AttendanceSession.employee_id == employee_id,
                                              or_(completed, running, missing))
    if exclude_id is not None:
        stmt = stmt.where(AttendanceSession.id != exclude_id)
    return db.scalar(stmt.limit(1)) is not None


def submit(
    db: Session, user: User, *, attendance_date: date, check_in_time: time, check_out_time: time,
    check_out_next_day: bool, break_minutes: int, reason: str, session_id: int | None, ctx: RequestContext,
) -> AttendanceCorrection:
    tz = office_tz()
    now = clock.utcnow()
    today = local_date(now, tz)
    policy = get_policy(db)
    reason = reason.strip()
    if len(reason) < 5:
        raise ValidationFailed("Please explain the reason for the correction (at least 5 characters)")
    if attendance_date > today:
        raise ValidationFailed("Corrections cannot be requested for future dates")
    if attendance_date < today - timedelta(days=policy.correction_window_days):
        raise ValidationFailed(f"Corrections can only be requested for the last {policy.correction_window_days} days")
    if attendance_date < user.date_joined:
        raise ValidationFailed("Corrections cannot be requested before your joining date")

    start = local_to_utc(attendance_date, check_in_time, tz)
    end = local_to_utc(attendance_date + timedelta(days=1 if check_out_next_day else 0), check_out_time, tz)
    try:
        session_net_minutes(start, end, break_minutes)
    except InvalidSessionTimes as exc:
        raise ValidationFailed(str(exc)) from None
    if end - start > timedelta(minutes=policy.max_session_minutes):
        raise ValidationFailed(f"A session cannot be longer than {policy.max_session_minutes // 60} hours")
    if end > now:
        raise ValidationFailed("Requested check-out time cannot be in the future")

    original = None
    if session_id is not None:
        original = db.get(AttendanceSession, session_id)
        if original is None or original.employee_id != user.id:
            raise NotFound("Attendance session not found")
        if original.status == SessionStatus.VOIDED:
            raise ValidationFailed("This session has already been replaced by a correction")
        if original.status == SessionStatus.OPEN and not is_unresolved(session_data(original), today, now, tz,
                                                                         policy_data(policy)):
            raise ValidationFailed("Check out of your active session before requesting a correction for it")
        if local_date(original.check_in_at, tz) != attendance_date:
            raise ValidationFailed("The selected session does not belong to the attendance date")
        pending = db.scalar(select(AttendanceCorrection.id).where(
            AttendanceCorrection.original_session_id == original.id,
            AttendanceCorrection.status == RequestStatus.PENDING))
        if pending is not None:
            raise Conflict("A correction for this session is already pending")

    if _overlaps(db, user.id, start, end, original.id if original else None):
        raise ValidationFailed("The requested times overlap another recorded session")

    correction = AttendanceCorrection(
        employee_id=user.id, attendance_date=attendance_date,
        request_type=CorrectionType.MODIFY_SESSION if original else CorrectionType.ADD_SESSION,
        original_session_id=original.id if original else None,
        original_values=session_snapshot(original) if original else None,
        requested_check_in_at=start, requested_check_out_at=end, requested_break_minutes=break_minutes,
        reason=reason, status=RequestStatus.PENDING,
    )
    db.add(correction)
    db.flush()
    audit.record(db, actor_id=user.id, action="CORRECTION_SUBMITTED", entity_type="attendance_correction",
                 entity_id=correction.id, after=correction_snapshot(correction), ctx=ctx)
    db.commit()
    return correction


def ensure_can_review(db: Session, reviewer: User, employee_id: int) -> None:
    if reviewer.id == employee_id:
        raise Forbidden("You cannot review your own request")
    if reviewer.role == Role.ADMIN:
        return
    if reviewer.role == Role.MANAGER and get_policy(db).manager_can_approve \
            and employee_id in subtree_ids(db, reviewer.id):
        return
    raise Forbidden("You are not authorised to review this request")


def _get_pending_locked(db: Session, correction_id: int, reviewer: User) -> AttendanceCorrection:
    c = db.execute(select(AttendanceCorrection).where(AttendanceCorrection.id == correction_id)
                   .with_for_update(of=AttendanceCorrection)).unique().scalar_one_or_none()
    if c is None or not can_view_employee(db, reviewer, c.employee_id):
        raise NotFound("Correction request not found")
    ensure_can_review(db, reviewer, c.employee_id)
    if c.status != RequestStatus.PENDING:
        raise Conflict(f"This request has already been {c.status.value.lower()}")
    return c


def approve(db: Session, reviewer: User, correction_id: int, comment: str | None,
            ctx: RequestContext) -> AttendanceCorrection:
    c = _get_pending_locked(db, correction_id, reviewer)
    # Lock the employee row so concurrent check-ins/approvals cannot interleave.
    db.execute(select(User.id).where(User.id == c.employee_id).with_for_update())
    before = correction_snapshot(c)
    now = clock.utcnow()

    original = db.get(AttendanceSession, c.original_session_id) if c.original_session_id else None
    if c.original_session_id and (original is None or original.status == SessionStatus.VOIDED):
        raise Conflict("The original session has changed since this request was made; ask for a new request")
    if _overlaps(db, c.employee_id, c.requested_check_in_at, c.requested_check_out_at,
                 original.id if original else None):
        raise Conflict("The requested times now overlap another recorded session")

    if original is not None:
        original_before = session_snapshot(original)
        original.status = SessionStatus.VOIDED
        original.voided_at = now
        original.void_reason = f"Replaced by correction #{c.id}"
        db.flush()
        audit.record(db, actor_id=reviewer.id, action="SESSION_VOIDED", entity_type="attendance_session",
                     entity_id=original.id, before=original_before, after=session_snapshot(original), ctx=ctx)

    new_session = AttendanceSession(
        employee_id=c.employee_id, check_in_at=c.requested_check_in_at, check_out_at=c.requested_check_out_at,
        break_minutes=c.requested_break_minutes,
        net_minutes=session_net_minutes(c.requested_check_in_at, c.requested_check_out_at,
                                        c.requested_break_minutes),
        status=SessionStatus.COMPLETED, source=SessionSource.CORRECTION, notes=f"Correction #{c.id}",
        work_location=original.work_location if original else None,  # a time fix keeps the Office / WFH choice
    )
    db.add(new_session)
    try:
        db.flush()  # PostgreSQL's exclusion constraint is the final guard against overlaps
    except IntegrityError:
        db.rollback()
        raise Conflict("The requested times overlap another recorded session") from None
    c.status = RequestStatus.APPROVED
    c.reviewed_by_id = reviewer.id
    c.reviewed_at = now
    c.reviewer_comment = (comment or "").strip() or None
    c.resulting_session_id = new_session.id
    audit.record(db, actor_id=reviewer.id, action="CORRECTION_APPROVED", entity_type="attendance_correction",
                 entity_id=c.id, before=before, after=correction_snapshot(c), ctx=ctx)
    tz = office_tz()
    days = {local_date(new_session.check_in_at, tz)} | ({local_date(original.check_in_at, tz)} if original else set())
    wfo.refresh_days(db, db.get(User, c.employee_id), days)
    db.commit()
    db.refresh(c)
    return c


def reject(db: Session, reviewer: User, correction_id: int, comment: str, ctx: RequestContext):
    comment = (comment or "").strip()
    if not comment:
        raise ValidationFailed("A comment is required when rejecting a request")
    c = _get_pending_locked(db, correction_id, reviewer)
    before = correction_snapshot(c)
    c.status = RequestStatus.REJECTED
    c.reviewed_by_id = reviewer.id
    c.reviewed_at = clock.utcnow()
    c.reviewer_comment = comment
    audit.record(db, actor_id=reviewer.id, action="CORRECTION_REJECTED", entity_type="attendance_correction",
                 entity_id=c.id, before=before, after=correction_snapshot(c), ctx=ctx)
    db.commit()
    db.refresh(c)
    return c


def cancel(db: Session, user: User, correction_id: int, ctx: RequestContext):
    c = db.get(AttendanceCorrection, correction_id)
    if c is None or c.employee_id != user.id:
        raise NotFound("Correction request not found")
    if c.status != RequestStatus.PENDING:
        raise Conflict("Only pending requests can be cancelled")
    c.status = RequestStatus.CANCELLED
    audit.record(db, actor_id=user.id, action="CORRECTION_CANCELLED", entity_type="attendance_correction",
                 entity_id=c.id, ctx=ctx)
    db.commit()
    db.refresh(c)
    return c
