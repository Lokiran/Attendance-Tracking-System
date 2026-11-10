"""Attendance / badge file imports for WFO: parse, preview, commit (or discard).

* Files: CSV (UTF-8, `,` `;` or tab) or Excel (.xlsx, first sheet), at most 5 MB and 20,000 rows.
* Headers and text values are matched through ``config/wfo_mapping.json`` (see ``app.core.wfo_mapping``).
  Required columns: employee (code or email) and date; plus at least one of work mode, hours, in / out
  times or source. Rows whose source is a badge / swipe value are badge evidence.
* Dates are read day-first (31/03/2026, 31-03-2026, 2026-03-31, 31-Mar-2026, Excel dates).
* Preview stores the parsed rows and what the commit would produce (counts per work mode, conflicts,
  days protected by a manual override). Nothing in attendance changes until commit.
* Commit upserts ``attendance_daily`` on (employee_id, work_date). A day with an Admin override is
  skipped unless ``overwrite_overrides`` is chosen (then the override is cleared and audited).
"""

from __future__ import annotations

import csv
import hashlib
import io
import re
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core import clock
from app.core.context import RequestContext
from app.core.errors import Conflict, NotFound, ValidationFailed
from app.core.timeutil import local_date, local_to_utc, office_tz
from app.core.wfo_mapping import get_mapping
from app.models import (
    AttendanceDaily,
    AttendanceUpload,
    AttendanceUploadRow,
    DailyStatus,
    UploadStatus,
    User,
    WorkMode,
)
from app.services import audit, wfo
from app.services.policy import get_policy

MAX_UPLOAD_BYTES = 5 * 1024 * 1024
MAX_ROWS = 20_000
DATE_FORMATS = ("%Y-%m-%d", "%d-%m-%Y", "%d/%m/%Y", "%d.%m.%Y", "%d-%b-%Y", "%d %b %Y", "%d-%B-%Y", "%d %B %Y",
                "%Y/%m/%d")
TIME_FORMATS = ("%H:%M", "%H:%M:%S", "%I:%M %p", "%I:%M:%S %p", "%I %p")
NOT_WORKING_MODES = (WorkMode.LEAVE, WorkMode.HOLIDAY, WorkMode.WEEKOFF)


# ------------------------------------------------------------------ reading files

def read_table(filename: str, content: bytes) -> list[list]:
    """Rows of cells (first sheet for Excel). Raises ValidationFailed for unsupported or broken files."""
    name = filename.lower()
    if len(content) > MAX_UPLOAD_BYTES:
        raise ValidationFailed(f"The file is larger than {MAX_UPLOAD_BYTES // (1024 * 1024)} MB")
    if not content:
        raise ValidationFailed("The file is empty")
    if name.endswith(".csv"):
        try:
            text = content.decode("utf-8-sig")
        except UnicodeDecodeError:
            text = content.decode("cp1252", errors="replace")
        sample = text[:4096]
        delimiter = max([",", ";", "\t"], key=sample.count)
        return [row for row in csv.reader(io.StringIO(text), delimiter=delimiter)]
    if name.endswith(".xlsx") or name.endswith(".xlsm"):
        from openpyxl import load_workbook
        try:
            wb = load_workbook(io.BytesIO(content), read_only=True, data_only=True)
        except Exception:  # noqa: BLE001 - any parse failure is a bad file for the user
            raise ValidationFailed("This Excel file could not be read") from None
        try:
            return [list(r) for r in wb.worksheets[0].iter_rows(values_only=True)]
        finally:
            wb.close()
    raise ValidationFailed("Upload a .csv or .xlsx file")


def _cell_text(v) -> str:
    if v is None:
        return ""
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    return str(v).strip()


def parse_date(v) -> date | None:
    if isinstance(v, datetime):
        return v.date()
    if isinstance(v, date):
        return v
    text = _cell_text(v)
    for fmt in DATE_FORMATS:
        try:
            return datetime.strptime(text, fmt).date()
        except ValueError:
            continue
    return None


def parse_hours(v) -> int | None:
    """Minutes from 7.5 / "7.5" / "7:30" / an Excel time. None if empty; ValueError if invalid."""
    if v is None or _cell_text(v) == "":
        return None
    if isinstance(v, time):
        return v.hour * 60 + v.minute
    if isinstance(v, timedelta):
        minutes = int(v.total_seconds() // 60)
    elif isinstance(v, (int, float)):
        minutes = round(float(v) * 60)
    else:
        text = _cell_text(v).lower().replace("hrs", "").replace("h", "").strip()
        match = re.fullmatch(r"(\d{1,2}):(\d{2})", text)
        minutes = int(match[1]) * 60 + int(match[2]) if match else round(float(text) * 60)
    if not 0 <= minutes <= 24 * 60:
        raise ValueError("hours must be between 0 and 24")
    return minutes


def parse_time(v) -> time | None:
    if v is None or _cell_text(v) == "":
        return None
    if isinstance(v, datetime):
        return v.time()
    if isinstance(v, time):
        return v
    text = _cell_text(v).upper()
    for fmt in TIME_FORMATS:
        try:
            return datetime.strptime(text, fmt).time()
        except ValueError:
            continue
    raise ValueError(f"time {text!r} is not like 09:30 or 6:15 PM")


# ------------------------------------------------------------------ parsing rows

@dataclass
class Parsed:
    row_number: int
    employee_ref: str | None = None
    employee_id: int | None = None
    work_date: date | None = None
    raw_work_mode: str | None = None
    work_mode: WorkMode | None = None
    raw_source: str | None = None
    is_badge: bool = False
    minutes: int | None = None
    first_in: datetime | None = None
    last_out: datetime | None = None
    error: str | None = None


def parse_rows(db: Session, table: list[list], today: date) -> tuple[list[Parsed], dict[str, str]]:
    mapping = get_mapping()
    rows = [r for r in table if any(_cell_text(c) for c in r)]
    if not rows:
        raise ValidationFailed("The file has no rows")
    header = rows[0]
    columns: dict[str, int] = {}
    for i, h in enumerate(header):
        key = mapping.column(_cell_text(h))
        if key and key not in columns:
            columns[key] = i
    missing = [k for k in ("employee", "date") if k not in columns]
    if missing:
        raise ValidationFailed(f"Missing column(s): {', '.join(missing)}. Found: "
                               f"{', '.join(_cell_text(h) for h in header if _cell_text(h)) or 'none'}")
    if not ({"work_mode", "hours", "in_time", "source"} & columns.keys()):
        raise ValidationFailed("Add a work mode, hours, in / out time or source column: nothing to import otherwise")
    if len(rows) - 1 > MAX_ROWS:
        raise ValidationFailed(f"At most {MAX_ROWS} rows per file")

    people = db.execute(select(User.id, func.lower(User.employee_code), func.lower(User.email), User.date_joined)).all()
    by_ref = {}
    for uid, code, email, joined in people:
        by_ref[code] = (uid, joined)
        by_ref[email] = (uid, joined)
    tz = office_tz()

    def cell(r: list, key: str):
        i = columns.get(key)
        return r[i] if i is not None and i < len(r) else None

    out: list[Parsed] = []
    for n, r in enumerate(rows[1:], start=2):
        p = Parsed(row_number=n, employee_ref=_cell_text(cell(r, "employee"))[:255] or None)
        try:
            ref = (p.employee_ref or "").lower()
            if not ref:
                raise ValueError("employee is empty")
            if ref not in by_ref:
                raise ValueError(f"unknown employee {p.employee_ref!r}")
            p.employee_id, joined = by_ref[ref]
            p.work_date = parse_date(cell(r, "date"))
            if p.work_date is None:
                raise ValueError(f"date {_cell_text(cell(r, 'date'))!r} is not a date (use 31/03/2026 or 2026-03-31)")
            p.raw_work_mode = _cell_text(cell(r, "work_mode"))[:64] or None
            if p.raw_work_mode:
                p.work_mode = mapping.work_mode(p.raw_work_mode)
                if p.work_mode is None:
                    raise ValueError(f"unknown work mode {p.raw_work_mode!r} (accepted values are in "
                                     "config/wfo_mapping.json)")
            p.raw_source = _cell_text(cell(r, "source"))[:64] or None
            p.is_badge = mapping.is_badge(p.raw_source)
            p.minutes = parse_hours(cell(r, "hours"))
            t_in, t_out = parse_time(cell(r, "in_time")), parse_time(cell(r, "out_time"))
            if t_in:
                p.first_in = local_to_utc(p.work_date, t_in, tz)
            if t_in and t_out:
                out_day = p.work_date + timedelta(days=1) if t_out <= t_in else p.work_date  # overnight
                p.last_out = local_to_utc(out_day, t_out, tz)
                if p.minutes is None:
                    p.minutes = int((p.last_out - p.first_in).total_seconds() // 60)
            if p.work_date < joined:
                raise ValueError("date is before the employee joined")
            if p.work_date > today and p.work_mode not in NOT_WORKING_MODES:
                raise ValueError("future dates are only accepted for leave, holiday or weekly off")
            if p.work_mode is None and p.minutes is None and not p.is_badge:
                raise ValueError("nothing to import on this row (no work mode, hours, times or badge source)")
        except ValueError as exc:
            p.error = str(exc)[:255]
        out.append(p)
    return out, {k: _cell_text(header[i]) for k, i in columns.items()}


# ------------------------------------------------------------------ evidence per employee-day

@dataclass
class DayUpdate:
    """What a file says about one employee-day (only the sources present in the file are changed)."""
    has_upload: bool = False
    upload_mode: WorkMode | None = None
    upload_minutes: int | None = None
    has_badge: bool = False
    badge_minutes: int = 0
    first_in: datetime | None = None
    last_out: datetime | None = None
    rows: list[int] = field(default_factory=list)


def aggregate(rows: list[Parsed] | list[AttendanceUploadRow]) -> tuple[dict[tuple[int, date], DayUpdate], int]:
    """Combine valid rows per (employee, date). Returns the updates and the number of duplicate rows."""
    updates: dict[tuple[int, date], DayUpdate] = defaultdict(DayUpdate)
    duplicates = 0
    for r in rows:
        if r.error or r.employee_id is None or r.work_date is None:
            continue
        u = updates[(r.employee_id, r.work_date)]
        u.rows.append(r.row_number)
        if r.is_badge:
            u.has_badge = True
            u.badge_minutes = max(u.badge_minutes, r.minutes or 0)
            u.first_in = min(filter(None, [u.first_in, r.first_in]), default=None)
            u.last_out = max(filter(None, [u.last_out, r.last_out]), default=None)
            if r.work_mode is not None:
                u.has_upload, u.upload_mode = True, r.work_mode
        else:
            if u.has_upload:
                duplicates += 1  # later rows for the same day win
            u.has_upload = True
            if r.work_mode is not None:
                u.upload_mode = r.work_mode
            if r.minutes is not None:
                u.upload_minutes = r.minutes
            if r.first_in and not u.first_in:
                u.first_in, u.last_out = r.first_in, r.last_out
    return dict(updates), duplicates


def _evidence_fields(u: DayUpdate) -> dict:
    fields = {}
    if u.has_upload:
        fields["upload_mode"] = u.upload_mode
        fields["upload_minutes"] = u.upload_minutes
    if u.has_badge:
        fields["badge_minutes"] = u.badge_minutes
    return fields


def _existing(db: Session, keys) -> dict[tuple[int, date], AttendanceDaily]:
    out = {}
    by_emp: dict[int, set[date]] = defaultdict(set)
    for eid, d in keys:
        by_emp[eid].add(d)
    for eid, dates in by_emp.items():
        for row in db.scalars(select(AttendanceDaily).where(AttendanceDaily.employee_id == eid,
                                                            AttendanceDaily.work_date.between(min(dates), max(dates)))):
            if row.work_date in dates:
                out[(eid, row.work_date)] = row
    return out


def _classify(db: Session, updates: dict[tuple[int, date], DayUpdate], existing, overwrite: bool):
    """Classify every affected day as the commit would (without writing)."""
    results = {}
    by_emp: dict[int, list[date]] = defaultdict(list)
    for eid, d in updates:
        by_emp[eid].append(d)
    users = {u.id: u for u in db.scalars(select(User).where(User.id.in_(list(by_emp)))).unique()} if by_emp else {}
    for eid, dates in by_emp.items():
        overrides = {}
        for d in dates:
            row = existing.get((eid, d))
            if row is not None and row.override_work_mode is not None and not overwrite:
                continue  # protected: stays as it is
            fields = _evidence_fields(updates[(eid, d)])
            if overwrite:
                fields["override"] = None
            overrides[(eid, d)] = fields
        ed = wfo.classify_range(db, [users[eid]], min(dates), max(dates), overrides=overrides)[eid]
        for c in ed.days:
            if (eid, c.date) in updates:
                results[(eid, c.date)] = c
    return results, users


# ------------------------------------------------------------------ preview / commit / discard

def preview(db: Session, admin: User, filename: str, content: bytes, ctx: RequestContext) -> AttendanceUpload:
    today = local_date(clock.utcnow(), office_tz())
    parsed, columns = parse_rows(db, read_table(filename, content), today)
    upload = AttendanceUpload(filename=filename[:255], file_sha256=hashlib.sha256(content).hexdigest(),
                              file_size=len(content), status=UploadStatus.PREVIEW, uploaded_by_id=admin.id,
                              row_count=len(parsed), error_count=sum(1 for p in parsed if p.error))
    db.add(upload)
    db.flush()
    db.bulk_insert_mappings(AttendanceUploadRow, [{**p.__dict__, "upload_id": upload.id} for p in parsed])
    upload.summary = _summary(db, parsed, columns, overwrite=False)
    audit.record(db, actor_id=admin.id, action="ATTENDANCE_UPLOAD_PREVIEWED", entity_type="attendance_upload",
                 entity_id=upload.id, after={"filename": upload.filename, "rows": upload.row_count,
                                             "errors": upload.error_count}, ctx=ctx)
    db.commit()
    db.refresh(upload)
    return upload


def _summary(db: Session, rows, columns: dict[str, str], overwrite: bool) -> dict:
    updates, duplicates = aggregate(rows)
    existing = _existing(db, updates.keys())
    results, users = _classify(db, updates, existing, overwrite)
    policy = wfo.policy_of(get_policy(db))
    modes: Counter = Counter()
    conflicts = []
    office_below_minimum = 0
    for key, c in results.items():
        modes[c.cls.work_mode.value if c.cls.work_mode else "NONE"] += 1
        if c.cls.work_mode == WorkMode.OFFICE and c.cls.office_minutes < policy.min_office_minutes:
            office_below_minimum += 1
        if c.cls.conflicts:
            u = users[key[0]]
            conflicts.append({"employee_code": u.employee_code, "full_name": u.full_name, "date": key[1].isoformat(),
                              "conflicts": list(c.cls.conflicts), "rows": updates[key].rows})
    protected = [k for k in updates if existing.get(k) is not None and existing[k].override_work_mode is not None]
    conflicts.sort(key=lambda x: (x["date"], x["full_name"]))
    errors = [r for r in rows if r.error]
    return {
        "columns": columns,
        "rows": len(rows), "valid_rows": len(rows) - len(errors), "error_rows": len(errors),
        "badge_rows": sum(1 for r in rows if not r.error and r.is_badge),
        "duplicate_rows": duplicates,
        "employee_days": len(updates), "employees": len({k[0] for k in updates}),
        "new_days": sum(1 for k in updates if k not in existing),
        "by_work_mode": dict(modes),
        "office": modes.get("OFFICE", 0), "wfh": modes.get("WFH", 0),
        "office_below_minimum": office_below_minimum,
        "conflict_count": len(conflicts), "conflicts": conflicts[:200],
        "protected_overrides": len(protected),
        "errors": [{"row": r.row_number, "employee": r.employee_ref, "error": r.error} for r in errors[:200]],
        "date_from": min(k[1] for k in updates).isoformat() if updates else None,
        "date_to": max(k[1] for k in updates).isoformat() if updates else None,
    }


def _get(db: Session, upload_id: int) -> AttendanceUpload:
    upload = db.get(AttendanceUpload, upload_id)
    if upload is None:
        raise NotFound("Upload not found")
    return upload


def commit(db: Session, admin: User, upload_id: int, overwrite_overrides: bool, ctx: RequestContext) -> AttendanceUpload:
    upload = db.execute(select(AttendanceUpload).where(AttendanceUpload.id == upload_id)
                        .with_for_update()).scalar_one_or_none()
    if upload is None:
        raise NotFound("Upload not found")
    if upload.status != UploadStatus.PREVIEW:
        raise Conflict(f"This upload is already {upload.status.value.lower()}")
    rows = list(db.scalars(select(AttendanceUploadRow).where(AttendanceUploadRow.upload_id == upload.id)))
    updates, _duplicates = aggregate(rows)
    existing = _existing(db, updates.keys())
    applied = created = skipped = overwritten = 0
    touched: dict[int, set[date]] = defaultdict(set)
    now = clock.utcnow()
    for (eid, d), u in updates.items():
        row = existing.get((eid, d))
        if row is not None and row.override_work_mode is not None:
            if not overwrite_overrides:
                skipped += 1
                continue
            audit.record(db, actor_id=admin.id, action="WORK_MODE_OVERRIDE_CLEARED", entity_type="attendance_daily",
                         entity_id=f"{eid}:{d.isoformat()}",
                         before={"override_work_mode": row.override_work_mode, "override_note": row.override_note},
                         after={"override_work_mode": None, "via_upload": upload.id}, ctx=ctx)
            row.override_work_mode = row.override_note = row.overridden_by_id = row.overridden_at = None
            overwritten += 1
        if row is None:
            row = AttendanceDaily(employee_id=eid, work_date=d, office_minutes=0, wfh_minutes=0, corrected=False,
                                  conflicts=[], status=DailyStatus.ABSENT)
            db.add(row)
            created += 1
        if u.has_upload:
            row.upload_work_mode, row.upload_minutes = u.upload_mode, u.upload_minutes
        if u.has_badge:
            row.badge_minutes = u.badge_minutes
        if u.first_in:
            row.first_in, row.last_out = u.first_in, (u.last_out if u.last_out and u.last_out >= u.first_in else None)
        row.upload_id = upload.id
        row.computed_at = now
        touched[eid].add(d)
        applied += 1
    db.flush()
    policy_row = get_policy(db)
    users = {u.id: u for u in db.scalars(select(User).where(User.id.in_(list(touched)))).unique()} if touched else {}
    for eid, dates in touched.items():
        ed = wfo.classify_range(db, [users[eid]], min(dates), max(dates))[eid]
        wfo.store(db, ed, policy_row, only=dates)
    upload.status = UploadStatus.COMMITTED
    upload.committed_by_id = admin.id
    upload.committed_at = now
    upload.overwrite_overrides = overwrite_overrides
    upload.commit_summary = {"applied_days": applied, "created_days": created, "skipped_overrides": skipped,
                             "overrides_overwritten": overwritten, "error_rows": upload.error_count}
    audit.record(db, actor_id=admin.id, action="ATTENDANCE_UPLOAD_COMMITTED", entity_type="attendance_upload",
                 entity_id=upload.id, after={"filename": upload.filename, **upload.commit_summary}, ctx=ctx)
    db.commit()
    db.refresh(upload)
    return upload


def discard(db: Session, admin: User, upload_id: int, ctx: RequestContext) -> AttendanceUpload:
    upload = _get(db, upload_id)
    if upload.status != UploadStatus.PREVIEW:
        raise Conflict(f"This upload is already {upload.status.value.lower()}")
    upload.status = UploadStatus.DISCARDED
    audit.record(db, actor_id=admin.id, action="ATTENDANCE_UPLOAD_DISCARDED", entity_type="attendance_upload",
                 entity_id=upload.id, ctx=ctx)
    db.commit()
    db.refresh(upload)
    return upload


def recent(db: Session, limit: int = 50) -> list[AttendanceUpload]:
    return list(db.scalars(select(AttendanceUpload).order_by(AttendanceUpload.id.desc()).limit(limit)))


def get(db: Session, upload_id: int) -> AttendanceUpload:
    return _get(db, upload_id)
