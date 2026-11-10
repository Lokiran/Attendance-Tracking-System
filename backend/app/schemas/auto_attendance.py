from datetime import datetime

from pydantic import BaseModel, Field, field_validator

from app.schemas.common import ORMModel


class AutoAttendanceSettingsOut(ORMModel):
    """Rules every automatic-attendance job reads (stored in the attendance_policy row)."""
    auto_attendance_enabled: bool
    auto_shadow_mode: bool
    gap_minutes: int
    full_day_minutes: int
    half_day_minutes: int
    wifi_poll_minutes: int
    presence_poll_minutes: int
    no_data_alert_minutes: int
    raw_retention_days: int
    wfh_presence_states: list[str]
    updated_at: datetime


class AutoAttendanceSettingsUpdate(BaseModel):
    """Partial update; omitted fields keep their value. Ranges match the database CHECKs."""
    auto_attendance_enabled: bool | None = None
    auto_shadow_mode: bool | None = None
    gap_minutes: int | None = Field(None, ge=1, le=240)
    full_day_minutes: int | None = Field(None, ge=1, le=1440)
    half_day_minutes: int | None = Field(None, ge=0, le=1440)
    wifi_poll_minutes: int | None = Field(None, ge=1, le=60)
    presence_poll_minutes: int | None = Field(None, ge=1, le=60)
    no_data_alert_minutes: int | None = Field(None, ge=5, le=1440)
    raw_retention_days: int | None = Field(None, ge=7, le=3650)
    wfh_presence_states: list[str] | None = Field(None, min_length=1, max_length=20)

    @field_validator("wfh_presence_states")
    @classmethod
    def _clean_states(cls, v: list[str] | None) -> list[str] | None:
        if v is None:
            return v
        cleaned = list(dict.fromkeys(s.strip() for s in v if s and s.strip()))
        if not cleaned or any(len(s) > 32 for s in cleaned):
            raise ValueError("Give 1-20 Teams presence values of at most 32 characters")
        return cleaned
