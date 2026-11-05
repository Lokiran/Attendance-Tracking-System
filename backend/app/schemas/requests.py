from datetime import date, datetime, time

from pydantic import BaseModel, Field

from app.models import CorrectionType, LeaveType, RequestStatus
from app.schemas.common import ORMModel, UserBrief


class CorrectionCreate(BaseModel):
    attendance_date: date
    session_id: int | None = Field(None, description="Existing session to fix; omit to add a missing session")
    check_in_time: time = Field(description="Office-local wall-clock time")
    check_out_time: time = Field(description="Office-local wall-clock time")
    check_out_next_day: bool = Field(False, description="True for overnight sessions ending the next day")
    break_minutes: int = Field(0, ge=0, le=720)
    reason: str = Field(min_length=5, max_length=1000)


class ReviewRequest(BaseModel):
    comment: str | None = Field(None, max_length=1000)


class CorrectionOut(ORMModel):
    id: int
    employee: UserBrief
    attendance_date: date
    request_type: CorrectionType
    original_session_id: int | None
    original_values: dict | None
    requested_check_in_at: datetime
    requested_check_out_at: datetime
    requested_break_minutes: int
    reason: str
    status: RequestStatus
    reviewed_by: UserBrief | None
    reviewer_comment: str | None
    reviewed_at: datetime | None
    resulting_session_id: int | None
    created_at: datetime


class LeaveCreate(BaseModel):
    start_date: date
    end_date: date
    leave_type: LeaveType
    reason: str | None = Field(None, max_length=1000)


class LeaveRecordCreate(LeaveCreate):
    employee_id: int


class LeaveOut(ORMModel):
    id: int
    employee: UserBrief
    start_date: date
    end_date: date
    leave_type: LeaveType
    status: RequestStatus
    reason: str | None
    reviewed_by: UserBrief | None
    reviewed_at: datetime | None
    reviewer_comment: str | None
    created_at: datetime
