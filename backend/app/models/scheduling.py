from datetime import date, datetime, time

from sqlalchemy import JSON, Boolean, CheckConstraint, Date, ForeignKey, Index, Integer, String, Text, Time, false, text, true
from sqlalchemy.orm import Mapped, mapped_column

from app.core import clock
from app.db.base import Base, TimestampMixin
from app.db.types import UTCDateTime

DEFAULT_WFH_PRESENCE_STATES = ("Available", "Busy", "InAMeeting")

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
        CheckConstraint("gap_minutes BETWEEN 1 AND 240", name="gap_minutes_range"),
        CheckConstraint("full_day_minutes BETWEEN 1 AND 1440", name="full_day_minutes_range"),
        CheckConstraint("half_day_minutes >= 0 AND half_day_minutes <= full_day_minutes", name="half_day_minutes_range"),
        CheckConstraint("wifi_poll_minutes BETWEEN 1 AND 60 AND presence_poll_minutes BETWEEN 1 AND 60",
                        name="poll_minutes_range"),
        CheckConstraint("no_data_alert_minutes BETWEEN 5 AND 1440", name="no_data_alert_minutes_range"),
        CheckConstraint("raw_retention_days BETWEEN 7 AND 3650", name="raw_retention_days_range"),
        CheckConstraint("min_wfo_days_per_week BETWEEN 0 AND 7", name="min_wfo_days_per_week_range"),
        CheckConstraint("min_wfo_days_per_month BETWEEN 0 AND 31", name="min_wfo_days_per_month_range"),
        CheckConstraint("min_office_minutes_for_wfo_day BETWEEN 0 AND 1440", name="min_office_minutes_range"),
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
    # Automatic attendance rules (migration 0003). Every job reads these; nothing is hard-coded.
    # Wi-Fi events closer together than this belong to one office session (a longer gap splits it).
    gap_minutes: Mapped[int] = mapped_column(Integer, default=30, server_default=text("30"), nullable=False)
    # Worked minutes (office, or WFH when there was no office Wi-Fi) for Present and for Half-day.
    full_day_minutes: Mapped[int] = mapped_column(Integer, default=480, server_default=text("480"), nullable=False)
    half_day_minutes: Mapped[int] = mapped_column(Integer, default=240, server_default=text("240"), nullable=False)
    # Collector intervals; each is also the bucket length of that source's events table.
    wifi_poll_minutes: Mapped[int] = mapped_column(Integer, default=3, server_default=text("3"), nullable=False)
    presence_poll_minutes: Mapped[int] = mapped_column(Integer, default=5, server_default=text("5"), nullable=False)
    # A collector with no events for this long is "down": affected days become No data, never Absent.
    no_data_alert_minutes: Mapped[int] = mapped_column(Integer, default=15, server_default=text("15"), nullable=False)
    # Raw Wi-Fi / presence events older than this are purged; daily summaries are kept.
    raw_retention_days: Mapped[int] = mapped_column(Integer, default=90, server_default=text("90"), nullable=False)
    # WFO policy (migration 0004). A day is a WFO day when its work mode is OFFICE and it has at
    # least this many office minutes; compliance needs the weekly and monthly minimums (prorated).
    min_wfo_days_per_week: Mapped[int] = mapped_column(Integer, default=3, server_default=text("3"), nullable=False)
    min_wfo_days_per_month: Mapped[int] = mapped_column(Integer, default=12, server_default=text("12"), nullable=False)
    min_office_minutes_for_wfo_day: Mapped[int] = mapped_column(Integer, default=240, server_default=text("240"),
                                                                nullable=False)
    # Teams availability or activity values that count as working from home.
    wfh_presence_states: Mapped[list[str]] = mapped_column(
        JSON, default=lambda: list(DEFAULT_WFH_PRESENCE_STATES),
        server_default=text("'[\"Available\", \"Busy\", \"InAMeeting\"]'"), nullable=False,
    )
    updated_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(),
                                                 onupdate=lambda: clock.utcnow(), nullable=False)
    updated_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    notes: Mapped[str | None] = mapped_column(Text)
