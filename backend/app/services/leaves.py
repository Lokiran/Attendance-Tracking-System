"""Leave requests. Only APPROVED leave affects attendance calculations."""

from datetime import date, timedelta

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.context import RequestContext
from app.core.errors import Conflict, NotFound, ValidationFailed
from app.core.timeutil import today_local
from app.models import LeaveRecord, LeaveType, RequestStatus, User
from app.services import audit
from app.services.corrections import ensure_can_review
from app.services.scope import can_view_employee

MAX_LEAVE_DAYS = 90


def snapshot(lv: LeaveRecord) -> dict:
    return {"employee_id": lv.employee_id, "start_date": lv.start_date, "end_date": lv.end_date,
            "leave_type": lv.leave_type, "status": lv.status, "reviewer_comment": lv.reviewer_comment}


def _validate(db: Session, employee_id: int, start: date, end: date) -> None:
    if end < start:
        raise ValidationFailed("End date must be on or after start date")
    if (end - start).days + 1 > MAX_LEAVE_DAYS:
        raise ValidationFailed(f"A single leave request cannot exceed {MAX_LEAVE_DAYS} days")
    clash = db.scalar(select(LeaveRecord.id).where(
        LeaveRecord.employee_id == employee_id,
        LeaveRecord.status.in_([RequestStatus.PENDING, RequestStatus.APPROVED]),
        LeaveRecord.start_date <= end, LeaveRecord.end_date >= start,
    ))
    if clash is not None:
        raise Conflict("This leave overlaps an existing pending or approved leave")


def request_leave(db: Session, user: User, start: date, end: date, leave_type: LeaveType, reason: str | None,
                  ctx: RequestContext) -> LeaveRecord:
    if start < today_local() - timedelta(days=30):
        raise ValidationFailed("Leave can only be requested up to 30 days in the past")
    _validate(db, user.id, start, end)
    lv = LeaveRecord(employee_id=user.id, start_date=start, end_date=end, leave_type=leave_type,
                     reason=(reason or "").strip() or None, status=RequestStatus.PENDING)
    db.add(lv)
    db.flush()
    audit.record(db, actor_id=user.id, action="LEAVE_REQUESTED", entity_type="leave_record", entity_id=lv.id,
                 after=snapshot(lv), ctx=ctx)
    db.commit()
    db.refresh(lv)
    return lv


def record_approved_leave(db: Session, admin: User, employee: User, start: date, end: date,
                          leave_type: LeaveType, reason: str | None, ctx: RequestContext) -> LeaveRecord:
    ensure_can_review(db, admin, employee.id)
    _validate(db, employee.id, start, end)
    lv = LeaveRecord(employee_id=employee.id, start_date=start, end_date=end, leave_type=leave_type,
                     reason=(reason or "").strip() or None, status=RequestStatus.APPROVED,
                     reviewed_by_id=admin.id, reviewed_at=clock.utcnow())
    db.add(lv)
    db.flush()
    audit.record(db, actor_id=admin.id, action="LEAVE_RECORDED", entity_type="leave_record", entity_id=lv.id,
                 after=snapshot(lv), ctx=ctx)
    db.commit()
    db.refresh(lv)
    return lv


def review(db: Session, reviewer: User, leave_id: int, approve: bool, comment: str | None,
           ctx: RequestContext) -> LeaveRecord:
    lv = db.execute(select(LeaveRecord).where(LeaveRecord.id == leave_id).with_for_update(of=LeaveRecord)
                    ).unique().scalar_one_or_none()
    if lv is None or not can_view_employee(db, reviewer, lv.employee_id):
        raise NotFound("Leave request not found")
    ensure_can_review(db, reviewer, lv.employee_id)
    if lv.status != RequestStatus.PENDING:
        raise Conflict(f"This request has already been {lv.status.value.lower()}")
    comment = (comment or "").strip() or None
    if not approve and not comment:
        raise ValidationFailed("A comment is required when rejecting a request")
    before = snapshot(lv)
    lv.status = RequestStatus.APPROVED if approve else RequestStatus.REJECTED
    lv.reviewed_by_id = reviewer.id
    lv.reviewed_at = clock.utcnow()
    lv.reviewer_comment = comment
    audit.record(db, actor_id=reviewer.id, action="LEAVE_APPROVED" if approve else "LEAVE_REJECTED",
                 entity_type="leave_record", entity_id=lv.id, before=before, after=snapshot(lv), ctx=ctx)
    db.commit()
    db.refresh(lv)
    return lv


def cancel(db: Session, user: User, leave_id: int, ctx: RequestContext) -> LeaveRecord:
    lv = db.get(LeaveRecord, leave_id)
    if lv is None or lv.employee_id != user.id:
        raise NotFound("Leave request not found")
    if lv.status != RequestStatus.PENDING:
        raise Conflict("Only pending requests can be cancelled")
    lv.status = RequestStatus.CANCELLED
    audit.record(db, actor_id=user.id, action="LEAVE_CANCELLED", entity_type="leave_record", entity_id=lv.id,
                 ctx=ctx)
    db.commit()
    db.refresh(lv)
    return lv
