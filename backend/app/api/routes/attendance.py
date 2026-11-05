from datetime import date
from typing import Annotated

from typing import Literal

from fastapi import APIRouter, Body, Query

from app.api.deps import DB, CurrentUser, Ctx, Paging, Range
from app.models import DayStatus, SessionStatus
from app.schemas.attendance import (
    CheckOutRequest,
    CurrentStatusOut,
    DayDetailOut,
    DayRecordOut,
    DaysPage,
    ScheduleBrief,
    SessionOut,
    SummaryOut,
)
from app.schemas.dashboard import TrendsOut
from app.services import attendance_service
from app.services.dashboard import bucketize
from app.services.scope import get_viewable_employee

router = APIRouter(prefix="/attendance", tags=["attendance"])


@router.get("/current", response_model=CurrentStatusOut, summary="Today's status and active session")
def current(user: CurrentUser, db: DB):
    data = attendance_service.current_status(db, user)
    sched = data["schedule"]
    return CurrentStatusOut(
        server_time=data["server_time"], today=data["today"], office_timezone=data["office_timezone"],
        today_record=DayRecordOut.of(data["today_record"]) if data["today_record"] else None,
        active_session=SessionOut.model_validate(data["active_session"]) if data["active_session"] else None,
        unresolved_session=(SessionOut.model_validate(data["unresolved_session"])
                            if data["unresolved_session"] else None),
        schedule=ScheduleBrief(name=sched.name, start_time=sched.start_time, end_time=sched.end_time,
                               required_minutes=sched.required_minutes, working_days=sorted(sched.working_days))
        if sched else None,
        max_session_minutes=data["max_session_minutes"],
    )


@router.post("/check-in", response_model=SessionOut, status_code=201,
             summary="Start an attendance session at the current server time")
def check_in(user: CurrentUser, db: DB, ctx: Ctx):
    return attendance_service.check_in(db, user, ctx)


@router.post("/check-out", response_model=SessionOut, summary="Close the active session at the current server time")
def check_out(user: CurrentUser, db: DB, ctx: Ctx,
              body: Annotated[CheckOutRequest | None, Body()] = None):
    return attendance_service.check_out(db, user, (body or CheckOutRequest()).break_minutes, ctx)


@router.get("/employees/{employee_id}/summary", response_model=SummaryOut)
def employee_summary(employee_id: int, rng: Range, user: CurrentUser, db: DB):
    employee = get_viewable_employee(db, user, employee_id)
    summary, _ = attendance_service.summary_for(db, employee, rng.start, rng.end)
    return SummaryOut.of(summary)


@router.get("/employees/{employee_id}/trends", response_model=TrendsOut,
            summary="Day/week/month buckets for one employee")
def employee_trends(employee_id: int, rng: Range, user: CurrentUser, db: DB,
                    granularity: Literal["day", "week", "month"] = "month"):
    employee = get_viewable_employee(db, user, employee_id)
    records = attendance_service.records_for(db, employee, rng.start, rng.end)
    return {"granularity": granularity, "headcount": 1, "items": bucketize(records, rng.end, granularity)}


@router.get("/employees/{employee_id}/days", response_model=DaysPage, summary="Daily records (newest first)")
def employee_days(employee_id: int, rng: Range, paging: Paging, user: CurrentUser, db: DB,
                  status: DayStatus | None = None, exceptions_only: bool = False,
                  order: str = Query("desc", pattern="^(asc|desc)$")):
    employee = get_viewable_employee(db, user, employee_id)
    records = attendance_service.records_for(db, employee, rng.start, rng.end)
    if status:
        records = [r for r in records if r.status == status]
    if exceptions_only:
        records = [r for r in records if r.exceptions]
    if order == "desc":
        records.reverse()
    page = records[paging.offset:paging.offset + paging.page_size]
    return DaysPage(items=[DayRecordOut.of(r) for r in page], total=len(records), page=paging.page,
                    page_size=paging.page_size)


@router.get("/employees/{employee_id}/days/{day}", response_model=DayDetailOut,
            summary="One day's record with its underlying sessions")
def employee_day(employee_id: int, day: date, user: CurrentUser, db: DB):
    employee = get_viewable_employee(db, user, employee_id)
    records = attendance_service.records_for(db, employee, day, day)
    sessions = attendance_service.sessions_on(db, employee.id, day)
    return DayDetailOut(
        record=DayRecordOut.of(records[0]) if records else None,
        sessions=[SessionOut.model_validate(s) for s in sessions if s.status != SessionStatus.VOIDED],
        voided_sessions=[SessionOut.model_validate(s) for s in sessions if s.status == SessionStatus.VOIDED],
    )
