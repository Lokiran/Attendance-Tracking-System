from datetime import date
from typing import Generic, TypeVar

from pydantic import BaseModel, ConfigDict, Field, model_validator

from app.core.config import get_settings
from app.core.errors import ValidationFailed
from app.models import Role

T = TypeVar("T")


class ORMModel(BaseModel):
    model_config = ConfigDict(from_attributes=True)


class Page(BaseModel, Generic[T]):
    items: list[T]
    total: int
    page: int
    page_size: int


class PageParams(BaseModel):
    page: int = Field(1, ge=1)
    page_size: int = Field(20, ge=1, le=200)

    @property
    def offset(self) -> int:
        return (self.page - 1) * self.page_size


class DateRange(BaseModel):
    start: date
    end: date

    @model_validator(mode="after")
    def _check(self) -> "DateRange":
        if self.end < self.start:
            raise ValidationFailed("End date must be on or after start date")
        if (self.end - self.start).days + 1 > get_settings().max_report_days:
            raise ValidationFailed(f"Date range cannot exceed {get_settings().max_report_days} days")
        return self


class DepartmentBrief(ORMModel):
    id: int
    name: str


class UserBrief(ORMModel):
    id: int
    employee_code: str
    full_name: str
    role: Role
    job_title: str | None = None


class MessageResponse(BaseModel):
    message: str
