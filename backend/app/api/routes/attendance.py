"""Attendance read endpoints. Attendance comes from imported timesheets; there is no check-in / check-out."""

from datetime import date
from typing import Literal

from fastapi import APIRouter, Query

from app.api.deps import DB, CurrentUser, Paging, Range
from app.models import AttendanceUpload, DayStatus, User
from app.schemas.attendance import (
    CurrentStatusOut,
    DayDetailOut,
    DayRecordOut,
    DaysPage,
    ScheduleBrief,
    SummaryOut,
    TimesheetEntryOut,
)
from app.schemas.dashboard import TrendsOut
from app.services import attendance_service, wfo
from app.services.dashboard import bucketize
from app.services.scope import get_viewable_employee

router = APIRouter(prefix="/attendance", tags=["attendance"])


@router.get("/current", response_model=CurrentStatusOut, summary="Today, my shift and how far timesheets reach")
def current(user: CurrentUser, db: DB):
    data = attendance_service.current_status(db, user)
    sched = data["schedule"]
    return CurrentStatusOut(
        server_time=data["server_time"], today=data["today"], office_timezone=data["office_timezone"],
        schedule=ScheduleBrief(name=sched.name, start_time=sched.start_time, end_time=sched.end_time,
                               required_minutes=sched.required_minutes, working_days=sorted(sched.working_days))
        if sched else None,
        timesheet_through=data["timesheet_through"], my_timesheet_through=data["my_timesheet_through"],
        last_import_at=data["last_import_at"],
    )


@router.get("/employees/{employee_id}/summary", response_model=SummaryOut)
def employee_summary(employee_id: int, rng: Range, user: CurrentUser, db: DB):
    employee = get_viewable_employee(db, user, employee_id)
    summary, _ = attendance_service.summary_for(db, employee, rng.start, rng.end)
    out = SummaryOut.of(summary)
    out.wfo = wfo.range_summary(db, employee, rng.start, rng.end)
    return out


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
        records = list(reversed(records))
    page = records[paging.offset:paging.offset + paging.page_size]
    return DaysPage(items=[DayRecordOut.of(r) for r in page], total=len(records), page=paging.page,
                    page_size=paging.page_size)


@router.get("/employees/{employee_id}/days/{day}", response_model=DayDetailOut,
            summary="One day's record and the timesheet entry behind it")
def employee_day(employee_id: int, day: date, user: CurrentUser, db: DB):
    employee = get_viewable_employee(db, user, employee_id)
    records = attendance_service.records_for(db, employee, day, day)
    row = attendance_service.timesheet_row(db, employee.id, day)
    entry = None
    if row is not None:
        upload = db.get(AttendanceUpload, row.upload_id) if row.upload_id else None
        by = db.get(User, row.overridden_by_id) if row.overridden_by_id else None
        entry = TimesheetEntryOut(
            work_mode=row.work_mode, wfo_source=row.wfo_source, upload_work_mode=row.upload_work_mode,
            upload_minutes=row.upload_minutes, badge_minutes=row.badge_minutes, first_in=row.first_in,
            last_out=row.last_out, upload_filename=upload.filename if upload else None,
            override_work_mode=row.override_work_mode, override_note=row.override_note,
            overridden_by_name=by.full_name if by else None, overridden_at=row.overridden_at,
            conflicts=list(row.conflicts or []),
        )
    return DayDetailOut(record=DayRecordOut.of(records[0]) if records else None, timesheet=entry)
