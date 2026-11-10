from datetime import date

from pydantic import BaseModel, EmailStr, Field

from app.models import Role
from app.schemas.common import DepartmentBrief, ORMModel


class LoginRequest(BaseModel):
    email: EmailStr
    password: str = Field(min_length=1, max_length=128)


class DevLoginRequest(BaseModel):
    email: EmailStr


class ProvidersOut(BaseModel):
    """Which sign-in methods the login page offers."""
    password: bool
    entra: bool
    dev: bool


class ManagerBrief(ORMModel):
    id: int
    full_name: str
    employee_code: str


class Profile(ORMModel):
    id: int
    employee_code: str
    full_name: str
    email: str
    role: Role
    job_title: str | None
    department: DepartmentBrief | None
    manager: ManagerBrief | None
    date_joined: date
    is_active: bool
    attendance_tracked: bool


class MeResponse(BaseModel):
    user: Profile
    csrf_token: str
    office_timezone: str
    has_reports: bool


class ChangePasswordRequest(BaseModel):
    current_password: str = Field(min_length=1, max_length=128)
    new_password: str = Field(min_length=1, max_length=128)
