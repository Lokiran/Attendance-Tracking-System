"""Attendance correction request endpoints."""

from typing import Literal

from fastapi import APIRouter, Query
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.api.deps import DB, CurrentUser, Ctx, Paging
from app.core.errors import Forbidden
from app.models import AttendanceCorrection, RequestStatus, Role, User
from app.schemas.common import Page
from app.schemas.requests import CorrectionCreate, CorrectionOut, ReviewRequest
from app.services import corrections
from app.services.scope import subtree_ids

router = APIRouter(tags=["requests"])

ScopeParam = Literal["mine", "review"]


def _scoped(db: Session, user: User, model, scope: ScopeParam, employee_id: int | None):
    stmt = select(model)
    if scope == "mine":
        return stmt.where(model.employee_id == user.id)
    if user.role == Role.EMPLOYEE:
        raise Forbidden("Only managers and administrators can review requests")
    if user.role == Role.MANAGER:
        ids = subtree_ids(db, user.id)
        stmt = stmt.where(model.employee_id.in_(ids)) if ids else stmt.where(False)
    else:
        stmt = stmt.where(model.employee_id != user.id)
    if employee_id is not None:
        stmt = stmt.where(model.employee_id == employee_id)
    return stmt


def _page(db: Session, stmt, model, paging, schema):
    total = db.scalar(select(func.count()).select_from(stmt.subquery()))
    rows = db.scalars(stmt.order_by(model.created_at.desc(), model.id.desc())
                      .offset(paging.offset).limit(paging.page_size)).unique()
    return Page(items=[schema.model_validate(r) for r in rows], total=total, page=paging.page,
                page_size=paging.page_size)


# ------------------------------------------------------------------ corrections

@router.get("/corrections", response_model=Page[CorrectionOut], summary="List my or reviewable corrections")
def list_corrections(user: CurrentUser, db: DB, paging: Paging, scope: ScopeParam = "mine",
                     status: RequestStatus | None = None, employee_id: int | None = Query(None)):
    stmt = _scoped(db, user, AttendanceCorrection, scope, employee_id)
    if status:
        stmt = stmt.where(AttendanceCorrection.status == status)
    return _page(db, stmt, AttendanceCorrection, paging, CorrectionOut)


@router.post("/corrections", response_model=CorrectionOut, status_code=201)
def create_correction(body: CorrectionCreate, user: CurrentUser, db: DB, ctx: Ctx):
    return corrections.submit(
        db, user, attendance_date=body.attendance_date, check_in_time=body.check_in_time,
        check_out_time=body.check_out_time, check_out_next_day=body.check_out_next_day,
        break_minutes=body.break_minutes, reason=body.reason, session_id=body.session_id, ctx=ctx,
    )


@router.post("/corrections/{correction_id}/approve", response_model=CorrectionOut)
def approve_correction(correction_id: int, body: ReviewRequest, user: CurrentUser, db: DB, ctx: Ctx):
    return corrections.approve(db, user, correction_id, body.comment, ctx)


@router.post("/corrections/{correction_id}/reject", response_model=CorrectionOut)
def reject_correction(correction_id: int, body: ReviewRequest, user: CurrentUser, db: DB, ctx: Ctx):
    return corrections.reject(db, user, correction_id, body.comment or "", ctx)


@router.post("/corrections/{correction_id}/cancel", response_model=CorrectionOut)
def cancel_correction(correction_id: int, user: CurrentUser, db: DB, ctx: Ctx):
    return corrections.cancel(db, user, correction_id, ctx)
