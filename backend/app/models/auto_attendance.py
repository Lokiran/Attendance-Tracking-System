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
    UniqueConstraint, text
from sqlalchemy.orm import Mapped, mapped_column

from app.core import clock
from app.db.base import Base, TimestampMixin
from app.db.types import UTCDateTime
from app.models.enums import DailyStatus, RequestStatus, UploadStatus, WfoSource, WifiEventType, WorkMode
from app.models.system import BigId


def _mode_enum(name: str) -> Enum:
    return Enum(WorkMode, native_enum=False, length=16, create_constraint=True, name=name)


class AttendanceUpload(Base):
    """An imported attendance / badge file. Parsed rows are kept, so committing applies exactly
    what the preview showed (and the import stays traceable afterwards)."""

    __tablename__ = "attendance_uploads"
    __table_args__ = (Index("ix_attendance_uploads_created", "created_at"),)

    id: Mapped[int] = mapped_column(primary_key=True)
    filename: Mapped[str] = mapped_column(String(255), nullable=False)
    file_sha256: Mapped[str] = mapped_column(String(64), nullable=False)
    file_size: Mapped[int] = mapped_column(Integer, nullable=False)
    status: Mapped[UploadStatus] = mapped_column(
        Enum(UploadStatus, native_enum=False, length=16, create_constraint=True, name="upload_status"),
        nullable=False, default=UploadStatus.PREVIEW,
    )
    row_count: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    error_count: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    summary: Mapped[dict | None] = mapped_column(JSON)          # preview: counts by work mode, conflicts
    commit_summary: Mapped[dict | None] = mapped_column(JSON)   # what the commit applied / skipped
    overwrite_overrides: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    uploaded_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    created_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(), nullable=False)
    committed_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    committed_at: Mapped[datetime | None] = mapped_column(UTCDateTime)


class AttendanceUploadRow(Base):
    """One parsed row of an upload. ``error`` is set when the row cannot be applied."""

    __tablename__ = "attendance_upload_rows"
    __table_args__ = (
        Index("ix_attendance_upload_rows_upload", "upload_id", "row_number"),
        CheckConstraint("minutes IS NULL OR minutes >= 0", name="minutes_non_negative"),
    )

    id: Mapped[int] = mapped_column(BigId, primary_key=True)
    upload_id: Mapped[int] = mapped_column(ForeignKey("attendance_uploads.id", ondelete="CASCADE"), nullable=False)
    row_number: Mapped[int] = mapped_column(Integer, nullable=False)  # as in the file (header = row 1)
    employee_ref: Mapped[str | None] = mapped_column(String(255))     # employee code or email as written
    employee_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"))
    work_date: Mapped[date | None] = mapped_column(Date)
    raw_work_mode: Mapped[str | None] = mapped_column(String(64))
    work_mode: Mapped[WorkMode | None] = mapped_column(_mode_enum("upload_row_work_mode"))
    raw_source: Mapped[str | None] = mapped_column(String(64))
    is_badge: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    minutes: Mapped[int | None] = mapped_column(Integer)
    first_in: Mapped[datetime | None] = mapped_column(UTCDateTime)
    last_out: Mapped[datetime | None] = mapped_column(UTCDateTime)
    error: Mapped[str | None] = mapped_column(String(255))


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

    # --- WFO (migration 0004) -------------------------------------------------------------------
    # Result of the classification (app/services/wfo_rules.py). Recomputed whenever this row's
    # evidence, an override or the day's check-ins change; reads also recompute from the evidence.
    work_mode: Mapped[WorkMode | None] = mapped_column(_mode_enum("work_mode"))  # NULL: absent / no data
    wfo_source: Mapped[WfoSource | None] = mapped_column(
        Enum(WfoSource, native_enum=False, length=20, create_constraint=True, name="wfo_source"))
    # Human-readable notes when signals disagreed (e.g. "UPLOAD_COLUMN said WFH; office signal from BADGE").
    conflicts: Mapped[list[str]] = mapped_column(JSON, default=list, server_default=text("'[]'"), nullable=False)
    # Evidence, one column per source. NULL means "this source says nothing about the day".
    upload_work_mode: Mapped[WorkMode | None] = mapped_column(_mode_enum("upload_work_mode"))
    upload_minutes: Mapped[int | None] = mapped_column(Integer)   # hours column of the imported row
    badge_minutes: Mapped[int | None] = mapped_column(Integer)    # not NULL: badge / swipe rows exist
    wifi_minutes: Mapped[int | None] = mapped_column(Integer)     # office Wi-Fi minutes (automatic attendance)
    upload_id: Mapped[int | None] = mapped_column(ForeignKey("attendance_uploads.id", ondelete="SET NULL"))
    # Admin override (wfo_source MANUAL_OVERRIDE); the audit log keeps who, when, old and new.
    override_work_mode: Mapped[WorkMode | None] = mapped_column(_mode_enum("override_work_mode"))
    override_note: Mapped[str | None] = mapped_column(String(500))
    overridden_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    overridden_at: Mapped[datetime | None] = mapped_column(UTCDateTime)


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
