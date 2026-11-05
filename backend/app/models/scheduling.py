from datetime import date, datetime, time

from sqlalchemy import JSON, Boolean, CheckConstraint, Date, ForeignKey, Index, Integer, String, Text, Time, false, text, true
from sqlalchemy.orm import Mapped, mapped_column

from app.core import clock
from app.db.base import Base, TimestampMixin
from app.db.types import UTCDateTime


class WorkSchedule(TimestampMixin, Base):
    """Working pattern. Resolution order for a day: employee-specific > department > organisation default
    (both target ids NULL); within a tier, the assignment effective on that date wins."""

    __tablename__ = "work_schedules"
    __table_args__ = (
        CheckConstraint("NOT (employee_id IS NOT NULL AND department_id IS NOT NULL)", name="single_target"),
        CheckConstraint("required_minutes > 0 AND required_minutes <= 1440", name="required_minutes_range"),
        CheckConstraint("effective_to IS NULL OR effective_to >= effective_from", name="effective_range"),
        Index("ix_work_schedules_employee", "employee_id"),
        Index("ix_work_schedules_department", "department_id"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(100), nullable=False)
    employee_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"))
    department_id: Mapped[int | None] = mapped_column(ForeignKey("departments.id", ondelete="CASCADE"))
    start_time: Mapped[time] = mapped_column(Time, nullable=False)
    end_time: Mapped[time] = mapped_column(Time, nullable=False)  # earlier than start => overnight shift
    required_minutes: Mapped[int] = mapped_column(Integer, nullable=False)
    working_days: Mapped[list[int]] = mapped_column(JSON, nullable=False)  # ISO weekdays 1=Mon..7=Sun
    effective_from: Mapped[date] = mapped_column(Date, nullable=False)
    effective_to: Mapped[date | None] = mapped_column(Date)


class Holiday(Base):
    __tablename__ = "holidays"
    __table_args__ = (
        # A date can be a holiday once org-wide and once per department.
        Index("uq_holidays_date_department", "holiday_date", text("COALESCE(department_id, 0)"), unique=True),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(120), nullable=False)
    holiday_date: Mapped[date] = mapped_column(Date, nullable=False, index=True)
    department_id: Mapped[int | None] = mapped_column(ForeignKey("departments.id", ondelete="CASCADE"))
    created_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(), nullable=False)


class AttendancePolicy(Base):
    """Organisation-wide attendance rules (single row, id = 1)."""

    __tablename__ = "attendance_policy"
    __table_args__ = (
        CheckConstraint("id = 1", name="singleton"),
        CheckConstraint("min_present_minutes >= 0", name="min_present_non_negative"),
        CheckConstraint("max_session_minutes BETWEEN 60 AND 1440", name="max_session_range"),
    )

    id: Mapped[int] = mapped_column(primary_key=True, default=1)
    # Completed net minutes a finished workday needs to count as PRESENT (0 = any completed session).
    min_present_minutes: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    # If true, a day whose only sessions are unresolved (missing check-out) still counts as PRESENT.
    count_incomplete_as_present: Mapped[bool] = mapped_column(Boolean, default=False, nullable=False)
    # Open sessions older than this are treated as missing check-out and cannot be closed normally.
    max_session_minutes: Mapped[int] = mapped_column(Integer, default=960, nullable=False)
    late_grace_minutes: Mapped[int] = mapped_column(Integer, default=15, nullable=False)
    correction_window_days: Mapped[int] = mapped_column(Integer, default=30, nullable=False)
    manager_can_approve: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    # Automatic attendance (effective only while the AUTO_ATTENDANCE_ENABLED env switch is on).
    auto_attendance_enabled: Mapped[bool] = mapped_column(Boolean, default=False, server_default=false(),
                                                          nullable=False)
    auto_shadow_mode: Mapped[bool] = mapped_column(Boolean, default=True, server_default=true(), nullable=False)
    updated_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(),
                                                 onupdate=lambda: clock.utcnow(), nullable=False)
    updated_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    notes: Mapped[str | None] = mapped_column(Text)
