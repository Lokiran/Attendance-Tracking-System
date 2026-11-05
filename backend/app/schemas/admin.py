from datetime import date, datetime, time

from pydantic import BaseModel, EmailStr, Field

from app.models import Role
from app.schemas.auth import ManagerBrief
from app.schemas.common import DepartmentBrief, ORMModel


class UserCreate(BaseModel):
    employee_code: str = Field(min_length=2, max_length=32, pattern=r"^[A-Za-z0-9_-]+$")
    full_name: str = Field(min_length=2, max_length=120)
    email: EmailStr
    password: str = Field(min_length=1, max_length=128)
    role: Role = Role.EMPLOYEE
    job_title: str | None = Field(None, max_length=120)
    department_id: int | None = None
    manager_id: int | None = None
    date_joined: date | None = None
    attendance_tracked: bool = True


class UserUpdate(BaseModel):
    employee_code: str | None = Field(None, min_length=2, max_length=32, pattern=r"^[A-Za-z0-9_-]+$")
    full_name: str | None = Field(None, min_length=2, max_length=120)
    email: EmailStr | None = None
    role: Role | None = None
    job_title: str | None = Field(None, max_length=120)
    department_id: int | None = None
    manager_id: int | None = None
    is_active: bool | None = None
    attendance_tracked: bool | None = None
    date_joined: date | None = None


class PasswordReset(BaseModel):
    password: str = Field(min_length=1, max_length=128)


class UserAdminOut(ORMModel):
    id: int
    employee_code: str
    full_name: str
    email: str
    role: Role
    job_title: str | None
    department: DepartmentBrief | None
    manager: ManagerBrief | None
    is_active: bool
    attendance_tracked: bool
    date_joined: date
    created_at: datetime
    updated_at: datetime


class DepartmentCreate(BaseModel):
    name: str = Field(min_length=2, max_length=100)
    description: str | None = Field(None, max_length=2000)
    department_head_id: int | None = None


class DepartmentUpdate(BaseModel):
    name: str | None = Field(None, min_length=2, max_length=100)
    description: str | None = Field(None, max_length=2000)
    department_head_id: int | None = None


class DepartmentOut(ORMModel):
    id: int
    name: str
    description: str | None
    department_head_id: int | None
    head: ManagerBrief | None
    member_count: int = 0
    created_at: datetime


class ScheduleBase(BaseModel):
    name: str = Field(min_length=2, max_length=100)
    employee_id: int | None = None
    department_id: int | None = None
    start_time: time
    end_time: time
    required_minutes: int = Field(ge=1, le=1440)
    working_days: list[int] = Field(min_length=1, max_length=7)
    effective_from: date
    effective_to: date | None = None


class ScheduleUpdate(BaseModel):
    name: str | None = Field(None, min_length=2, max_length=100)
    start_time: time | None = None
    end_time: time | None = None
    required_minutes: int | None = Field(None, ge=1, le=1440)
    working_days: list[int] | None = Field(None, min_length=1, max_length=7)
    effective_from: date | None = None
    effective_to: date | None = None


class ScheduleOut(ORMModel):
    id: int
    name: str
    employee_id: int | None
    department_id: int | None
    start_time: time
    end_time: time
    required_minutes: int
    working_days: list[int]
    effective_from: date
    effective_to: date | None
    target_label: str = ""


class HolidayCreate(BaseModel):
    name: str = Field(min_length=2, max_length=120)
    holiday_date: date
    department_id: int | None = None


class HolidayOut(ORMModel):
    id: int
    name: str
    holiday_date: date
    department_id: int | None


class PolicyOut(ORMModel):
    min_present_minutes: int
    count_incomplete_as_present: bool
    max_session_minutes: int
    late_grace_minutes: int
    correction_window_days: int
    manager_can_approve: bool
    notes: str | None
    updated_at: datetime


class PolicyUpdate(BaseModel):
    min_present_minutes: int | None = Field(None, ge=0, le=1440)
    count_incomplete_as_present: bool | None = None
    max_session_minutes: int | None = Field(None, ge=60, le=1440)
    late_grace_minutes: int | None = Field(None, ge=0, le=240)
    correction_window_days: int | None = Field(None, ge=1, le=365)
    manager_can_approve: bool | None = None
    notes: str | None = Field(None, max_length=2000)


class AuditOut(ORMModel):
    id: int
    actor_user_id: int | None
    actor_name: str | None = None
    action: str
    entity_type: str
    entity_id: str | None
    before_values: dict | None
    after_values: dict | None
    request_id: str | None
    ip_address: str | None
    created_at: datetime
