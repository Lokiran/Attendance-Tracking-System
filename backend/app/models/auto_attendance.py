"""Automatic attendance: raw presence events, the computed day record and corrections to it.

Privacy: only presence and duration are stored. No browsing data, no traffic, no URLs, no IP history.

* ``wifi_events``: at most one row per person per time bucket (bucket length = the Wi-Fi poll
  interval from settings), so collectors can re-run safely: the unique key absorbs duplicates.
* ``presence_events``: the same for Microsoft Teams presence polls.
* ``attendance_daily``: one row per person per office-local work date, written by the daily
  rollup with an upsert.
* ``attendance_daily_corrections``: an employee asks to change a day; a manager or HR approves it.
"""

from datetime import date, datetime

from sqlalchemy import JSON, Boolean, CheckConstraint, Date, Enum, ForeignKey, Index, Integer, String, Text, \
    UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column

from app.core import clock
from app.db.base import Base, TimestampMixin
from app.db.types import UTCDateTime
from app.models.enums import DailyStatus, RequestStatus, WifiEventType
from app.models.system import BigId


class WifiEvent(Base):
    """A person's device was seen on the office Wi-Fi during one time bucket."""

    __tablename__ = "wifi_events"
    __table_args__ = (
        UniqueConstraint("employee_id", "ts_bucket", name="uq_wifi_events_employee_bucket"),
        Index("ix_wifi_events_ts_bucket", "ts_bucket"),  # sessionize by day, retention purge
        CheckConstraint("last_seen_at >= first_seen_at", name="seen_order"),
    )

    id: Mapped[int] = mapped_column(BigId, primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    ts_bucket: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)  # bucket start (UTC)
    first_seen_at: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)
    last_seen_at: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)
    # The last event seen in the bucket (CONNECT, DISCONNECT or a periodic SEEN / interim update).
    event_type: Mapped[WifiEventType] = mapped_column(
        Enum(WifiEventType, native_enum=False, length=16, create_constraint=True, name="wifi_event_type"),
        nullable=False,
    )
    source: Mapped[str] = mapped_column(String(32), nullable=False)  # adapter: mock, radius, cisco, aruba, meraki
    access_point: Mapped[str | None] = mapped_column(String(64))  # which AP / site; presence only
    created_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(), nullable=False)


class PresenceEvent(Base):
    """A person's Microsoft Teams presence in one poll bucket."""

    __tablename__ = "presence_events"
    __table_args__ = (
        UniqueConstraint("employee_id", "ts_bucket", name="uq_presence_events_employee_bucket"),
        Index("ix_presence_events_ts_bucket", "ts_bucket"),
    )

    id: Mapped[int] = mapped_column(BigId, primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    ts_bucket: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)
    polled_at: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)
    # Graph values are kept as strings: Microsoft adds new ones over time.
    availability: Mapped[str] = mapped_column(String(32), nullable=False)  # Available, Busy, Away, Offline, ...
    activity: Mapped[str | None] = mapped_column(String(32))  # InAMeeting, InACall, Presenting, ...
    source: Mapped[str] = mapped_column(String(32), nullable=False)  # graph, mock
    created_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(), nullable=False)


class AttendanceDaily(TimestampMixin, Base):
    """The computed attendance of one person on one office-local work date."""

    __tablename__ = "attendance_daily"
    __table_args__ = (
        UniqueConstraint("employee_id", "work_date", name="uq_attendance_daily_employee_date"),
        Index("ix_attendance_daily_date_status", "work_date", "status"),
        CheckConstraint("office_minutes >= 0 AND wfh_minutes >= 0", name="minutes_non_negative"),
        CheckConstraint("last_out IS NULL OR first_in IS NULL OR last_out >= first_in", name="in_out_order"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    work_date: Mapped[date] = mapped_column(Date, nullable=False)
    first_in: Mapped[datetime | None] = mapped_column(UTCDateTime)
    last_out: Mapped[datetime | None] = mapped_column(UTCDateTime)
    office_minutes: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    wfh_minutes: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    status: Mapped[DailyStatus] = mapped_column(
        Enum(DailyStatus, native_enum=False, length=16, create_constraint=True, name="daily_status"), nullable=False
    )
    # True once an approved correction changed this day; the rollup then leaves the corrected fields alone.
    corrected: Mapped[bool] = mapped_column(Boolean, default=False, nullable=False)
    computed_at: Mapped[datetime | None] = mapped_column(UTCDateTime)


class AttendanceDailyCorrection(TimestampMixin, Base):
    """A request to change one day's attendance. Approval updates ``attendance_daily``; the original
    values are kept here, and the decision is in the audit log."""

    __tablename__ = "attendance_daily_corrections"
    __table_args__ = (
        Index("ix_daily_corrections_employee_date", "employee_id", "work_date"),
        Index("ix_daily_corrections_status", "status"),
        CheckConstraint("requested_last_out IS NULL OR requested_first_in IS NULL "
                        "OR requested_last_out > requested_first_in", name="requested_order"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    work_date: Mapped[date] = mapped_column(Date, nullable=False)
    requested_first_in: Mapped[datetime | None] = mapped_column(UTCDateTime)
    requested_last_out: Mapped[datetime | None] = mapped_column(UTCDateTime)
    requested_status: Mapped[DailyStatus | None] = mapped_column(
        Enum(DailyStatus, native_enum=False, length=16, create_constraint=True, name="requested_daily_status")
    )
    reason: Mapped[str] = mapped_column(String(1000), nullable=False)
    original_values: Mapped[dict | None] = mapped_column(JSON)
    status: Mapped[RequestStatus] = mapped_column(
        Enum(RequestStatus, native_enum=False, length=16, create_constraint=True, name="daily_correction_status"),
        nullable=False, default=RequestStatus.PENDING,
    )
    reviewed_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    reviewed_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    reviewer_comment: Mapped[str | None] = mapped_column(Text)
