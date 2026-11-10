"""Attendance / badge file imports for WFO: parse, preview, commit (or discard).

* Files: CSV (UTF-8, `,` `;` or tab) or Excel (.xlsx), at most 5 MB and 20,000 rows. In a workbook the first
  sheet with an employee and a date column is read (or the sheet chosen); the header may be in any of the
  first 20 rows, and cells beside the data (a pivot table) are ignored.
* Headers and text values are matched through ``config/wfo_mapping.json`` (see ``app.core.wfo_mapping``).
  Required columns: employee (code or email) and date. With a work mode or source column each row says what
  the day was; without one the file is a presence list: every listed day is an Office day (or WFH, if
  chosen). Hours / in / out columns are optional. Rows whose source is a badge / swipe value are badge evidence.
* Dates are read day-first (31/03/2026, 31-03-2026, 2026-03-31, 31-Mar-2026, Excel dates); a date that only
  makes sense month-first within the months the file covers (09-01-2026 in a September file) is read so.
* Unknown employee IDs are listed; ``create_employees`` adds them (ID and name from the file) and re-checks.
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
from app.core.security import generate_token, hash_password
from app.core.timeutil import local_date, local_to_utc, office_tz
from app.core.wfo_mapping import get_mapping, normalize
from app.models import (
    AttendanceDaily,
    AttendanceUpload,
    AttendanceUploadRow,
    DailyStatus,
    Role,
    UploadStatus,
    User,
    WorkMode,
)
from app.services import audit, wfo
from app.services.admin_users import user_snapshot
from app.services.policy import get_policy

MAX_UPLOAD_BYTES = 5 * 1024 * 1024
MAX_ROWS = 20_000
DATE_FORMATS = ("%Y-%m-%d", "%d-%m-%Y", "%d/%m/%Y", "%d.%m.%Y", "%d-%b-%Y", "%d %b %Y", "%d-%B-%Y", "%d %B %Y",
                "%Y/%m/%d")
TIME_FORMATS = ("%H:%M", "%H:%M:%S", "%I:%M %p", "%I:%M:%S %p", "%I %p")
NOT_WORKING_MODES = (WorkMode.LEAVE, WorkMode.HOLIDAY, WorkMode.WEEKOFF)


# ------------------------------------------------------------------ reading files

HEADER_SEARCH_ROWS = 20
_MONTH_NAME = re.compile(r"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\b|\b\d{4}-\d{2}\b",
                         re.IGNORECASE)


@dataclass
class Table:
    rows: list[list]
    sheets: list[dict] = field(default_factory=list)  # Excel sheets: {"name", "usable", "rows"} (empty for CSV)
    sheet: str | None = None                           # the sheet that was read


def _csv_rows(content: bytes) -> list[list]:
    try:
        text = content.decode("utf-8-sig")
    except UnicodeDecodeError:
        text = content.decode("cp1252", errors="replace")
    delimiter = max([",", ";", "\t"], key=text[:4096].count)
    return [row for row in csv.reader(io.StringIO(text), delimiter=delimiter)]


def read_workbook(filename: str, content: bytes, sheet: str | None = None) -> Table:
    """Rows of the chosen sheet. Without ``sheet``, the first sheet whose header has an employee and a date
    column is used (an attendance workbook often starts with other sheets). Raises ValidationFailed for
    unsupported or broken files."""
    name = filename.lower()
    if len(content) > MAX_UPLOAD_BYTES:
        raise ValidationFailed(f"The file is larger than {MAX_UPLOAD_BYTES // (1024 * 1024)} MB")
    if not content:
        raise ValidationFailed("The file is empty")
    if name.endswith(".csv"):
        return Table(_csv_rows(content))
    if not (name.endswith(".xlsx") or name.endswith(".xlsm")):
        raise ValidationFailed("Upload a .csv or .xlsx file")
    from openpyxl import load_workbook
    try:
        wb = load_workbook(io.BytesIO(content), read_only=True, data_only=True)
    except Exception:  # noqa: BLE001 - any parse failure is a bad file for the user
        raise ValidationFailed("This Excel file could not be read") from None
    try:
        mapping = get_mapping()
        names = list(wb.sheetnames)
        data: dict[str, list[list]] = {}
        info = []
        for n in names:
            rows = [list(r) for r in wb[n].iter_rows(values_only=True)]
            found = find_header(rows, mapping)
            data[n] = rows
            info.append({"name": n, "usable": found is not None,
                         "rows": sum(1 for r in rows[found[0] + 1:] if any(_cell_text(c) for c in r)) if found else 0})
    finally:
        wb.close()
    if sheet is not None:
        if sheet not in data:
            raise ValidationFailed(f"There is no sheet {sheet!r}. Sheets: {', '.join(names)}")
        chosen = sheet
    else:
        # A monthly workbook keeps raw extracts next to the month's sheet ("September 2026"): prefer that one.
        usable = [i["name"] for i in info if i["usable"]]
        chosen = next((n for n in usable if _MONTH_NAME.search(n)), usable[0] if usable else names[0])
    return Table(data[chosen], info, chosen)


def read_table(filename: str, content: bytes) -> list[list]:
    return read_workbook(filename, content).rows


def _cell_text(v) -> str:
    if v is None:
        return ""
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    return str(v).strip()


def map_header(header: list, mapping) -> dict[str, int]:
    """Column key -> index. With both an ID column ("Employee ID") and a plain "Employee" column, the plain
    one holds the names."""
    candidates: dict[str, list[int]] = defaultdict(list)
    for i, h in enumerate(header):
        key = mapping.column(_cell_text(h))
        if key:
            candidates[key].append(i)
    columns = {k: v[0] for k, v in candidates.items()}
    ids = candidates.get("employee", [])
    if len(ids) > 1 and "name" not in columns:
        plain = [i for i in ids if normalize(_cell_text(header[i])) == "employee"]
        specific = [i for i in ids if i not in plain]
        if plain and specific:
            columns["employee"], columns["name"] = specific[0], plain[0]
    return columns


def find_header(rows: list[list], mapping) -> tuple[int, dict[str, int]] | None:
    """(row index, columns) of the first of the top rows that has an employee and a date column."""
    for i, row in enumerate(rows[:HEADER_SEARCH_ROWS]):
        columns = map_header(row, mapping)
        if "employee" in columns and "date" in columns:
            return i, columns
    return None


_NUMERIC_DATE = re.compile(r"^(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})$")


def parse_date(v) -> date | None:
    """One date, day-first for text (31/03/2026, 31-03-2026, 2026-03-31, 31-Mar-2026) or an Excel date."""
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


def date_candidates(v) -> list[tuple[date, bool]]:
    """Every reading of a date cell as (date, swapped). "15/09/2026" has one reading; "09-01-2026" has two
    (9 January day-first, 1 September month-first). An Excel date with a day up to 12 has two as well: as
    stored, and with day and month swapped (Excel on a US-format PC stores a typed 01/09/2026 as 9 January)."""
    if isinstance(v, (datetime, date)):
        d = parse_date(v)
        if d.day <= 12 and d.day != d.month:
            return [(d, False), (date(d.year, d.day, d.month), True)]
        return [(d, False)]
    text = _cell_text(v)
    m = _NUMERIC_DATE.match(text)
    if not m:
        d = parse_date(text)
        return [(d, False)] if d else []
    a, b, y = int(m[1]), int(m[2]), int(m[3])
    out: list[tuple[date, bool]] = []
    for day, month, month_first in ((a, b, False), (b, a, True)):
        try:
            d = date(y, month, day)
        except ValueError:
            continue
        if all(d != existing for existing, _ in out):
            out.append((d, month_first))
    return out


@dataclass
class DateReader:
    """Reads dates using the months the rest of the file is in. A monthly sheet often mixes 15/09/2026
    (day-first text) with 09-01-2026 (month-first) or Excel dates stored with day and month swapped (9 January
    for 1 September): unambiguous dates show which months the file covers, and an ambiguous date takes the
    reading that falls in those months (otherwise: day-first for text, as stored for an Excel date)."""
    months: set[tuple[int, int]]
    month_first: int = 0
    ambiguous_day_first: int = 0

    @classmethod
    def for_values(cls, values: list) -> "DateReader":
        counts: Counter = Counter()
        for v in values:
            c = date_candidates(v)
            if len(c) == 1:
                counts[(c[0][0].year, c[0][0].month)] += 1
        total = sum(counts.values())
        return cls({ym for ym, n in counts.items() if total and n >= 0.1 * total})

    def read(self, v) -> date | None:
        c = date_candidates(v)
        if not c:
            return None
        if len(c) == 1:
            return c[0][0]
        fitting = [x for x in c if (x[0].year, x[0].month) in self.months]
        if len(fitting) == 1:
            if fitting[0][1]:
                self.month_first += 1
            return fitting[0][0]
        if not isinstance(v, (datetime, date)):
            self.ambiguous_day_first += 1  # an Excel date stays as stored; a text date is read day-first
        return next(d for d, mf in c if not mf)


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
        match = re.fullmatch(r"(\d{1,2}):(\d{2})(?::\d{2})?", text)
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

UNKNOWN_PREFIX = "unknown employee "


def ref_key(ref: str | None) -> str:
    """How an employee reference is matched: case-insensitive, runs of spaces count as one."""
    return " ".join((ref or "").split()).lower()


ROW_COLUMNS = ("row_number", "employee_ref", "employee_id", "work_date", "raw_work_mode", "work_mode", "raw_source",
               "is_badge", "minutes", "first_in", "last_out", "error")


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
    employee_name: str | None = None  # from the file; not stored with the row

    def as_row(self) -> dict:
        return {k: getattr(self, k) for k in ROW_COLUMNS}


@dataclass
class ParseResult:
    rows: list[Parsed]
    columns: dict[str, str]
    presence_list: bool                       # no work mode / source column: every row is a presence day
    unknown: dict[str, dict]                  # ref_key -> {"ref", "names": Counter, "rows": n, "first": date}
    name_mismatches: list[dict]
    dates: DateReader
    blank_rows: int


def parse_rows(db: Session, table: list[list], today: date, presence_mode: WorkMode = WorkMode.OFFICE) -> ParseResult:
    mapping = get_mapping()
    found = find_header(table, mapping)
    if found is None:
        first = next((r for r in table if any(_cell_text(c) for c in r)), [])
        raise ValidationFailed("Could not find an employee and a date column in the first rows. Found: "
                               f"{', '.join(_cell_text(h) for h in first if _cell_text(h)) or 'nothing'}")
    header_at, columns = found
    header = table[header_at]
    body = table[header_at + 1:]
    if len(body) > MAX_ROWS:
        raise ValidationFailed(f"At most {MAX_ROWS} rows per file")
    # Without a work-mode or source column every listed day is presence in the chosen mode (an office
    # presence list by default); hours come from an hours or in / out column when there is one.
    presence_list = not ({"work_mode", "source"} & columns.keys())

    def cell(r: list, key: str):
        i = columns.get(key)
        return r[i] if i is not None and i < len(r) else None

    people = db.execute(select(User.id, func.lower(User.employee_code), func.lower(User.email), User.date_joined,
                               User.full_name)).all()
    by_ref = {}
    for uid, code, email, joined, full_name in people:
        by_ref[ref_key(code)] = (uid, joined, full_name)
        by_ref[ref_key(email)] = (uid, joined, full_name)
    tz = office_tz()
    dates = DateReader.for_values([cell(r, "date") for r in body])
    unknown: dict[str, dict] = {}
    mismatches: dict[str, dict] = {}
    out: list[Parsed] = []
    blank = 0
    for n, r in enumerate(body, start=header_at + 2):
        ref_text, date_value = _cell_text(cell(r, "employee")), cell(r, "date")
        if not ref_text and not _cell_text(date_value):
            blank += 1  # empty row, or only cells outside the columns we read (e.g. a pivot table beside the data)
            continue
        ref_text = " ".join(ref_text.split())
        p = Parsed(row_number=n, employee_ref=ref_text[:255] or None,
                   employee_name=" ".join(_cell_text(cell(r, "name")).split())[:120] or None)
        try:
            p.work_date = dates.read(date_value)
            ref = ref_key(ref_text)
            if not ref:
                raise ValueError("employee is empty")
            known = by_ref.get(ref)
            if known is None:
                entry = unknown.setdefault(ref, {"ref": p.employee_ref, "names": Counter(), "rows": 0,
                                                 "first": None})
                entry["rows"] += 1
                if p.employee_name:
                    entry["names"][p.employee_name] += 1
                if p.work_date and (entry["first"] is None or p.work_date < entry["first"]):
                    entry["first"] = p.work_date
                joined = date.min  # a person added from the file joins before their first date in it
            else:
                p.employee_id, joined, app_name = known
                if p.employee_name and normalize(p.employee_name) != normalize(app_name) \
                        and p.employee_ref not in mismatches:
                    mismatches[p.employee_ref] = {"employee_ref": p.employee_ref, "file_name": p.employee_name,
                                                  "app_name": app_name}
            if p.work_date is None:
                raise ValueError(f"date {_cell_text(date_value)!r} is not a date (use 31/03/2026 or 2026-03-31)")
            p.raw_work_mode = _cell_text(cell(r, "work_mode"))[:64] or None
            if p.raw_work_mode:
                p.work_mode = mapping.work_mode(p.raw_work_mode)
                if p.work_mode is None:
                    raise ValueError(f"unknown work mode {p.raw_work_mode!r} (accepted values are in "
                                     "config/wfo_mapping.json)")
            elif presence_list:
                p.work_mode = presence_mode
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
            if known is None:
                # Last check, so the row is otherwise complete and becomes valid once the person is added.
                raise ValueError(UNKNOWN_PREFIX + f"{p.employee_ref!r} (not in the app yet)")
        except ValueError as exc:
            p.error = str(exc)[:255]
        out.append(p)
    return ParseResult(out, {k: _cell_text(header[i]) for k, i in columns.items()}, presence_list, unknown,
                       list(mismatches.values()), dates, blank)


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

IMPORT_EMAIL_DOMAIN = "imported.example.com"  # placeholder address for people added from a file; edit it later
CODE_PATTERN = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9 ._-]*[A-Za-z0-9])?$")  # same rule as the Employees page


def _addable(ref: str) -> str | None:
    """Why an unknown reference cannot be added as a new employee (None if it can)."""
    if "@" in ref:
        return "this is an email address: add the person on the Employees page with their employee ID"
    if not 2 <= len(ref) <= 32 or not CODE_PATTERN.match(ref):
        return "not usable as an employee ID (2-32 letters, digits, spaces, '.', '-' or '_')"
    return None


def _unknown_list(result: ParseResult) -> list[dict]:
    out = []
    for e in result.unknown.values():
        names = [n for n, _c in e["names"].most_common()]
        reason = _addable(e["ref"])
        out.append({"employee_ref": e["ref"], "name": names[0] if names else None, "other_names": names[1:5],
                    "rows": e["rows"], "first_date": e["first"].isoformat() if e["first"] else None,
                    "can_add": reason is None, "reason": reason})
    out.sort(key=lambda x: (-x["rows"], x["employee_ref"].lower()))
    return out


def _date_notes(dates: DateReader) -> list[str]:
    months = ", ".join(date(y, m, 1).strftime("%B %Y") for y, m in sorted(dates.months))
    notes = []
    if dates.month_first:
        notes.append(f"{dates.month_first} date(s) had day and month swapped (like 09-01-2026, or an Excel date "
                     f"showing 9 January for 1 September) and were read in {months}, the month(s) the rest of "
                     "the file is in.")
    if dates.ambiguous_day_first:
        notes.append(f"{dates.ambiguous_day_first} date(s) like 05/06/2026 could be read either way and were read "
                     "day first (5 June).")
    return notes


def preview(db: Session, admin: User, filename: str, content: bytes, ctx: RequestContext, sheet: str | None = None,
            presence_mode: WorkMode = WorkMode.OFFICE) -> AttendanceUpload:
    if presence_mode not in (WorkMode.OFFICE, WorkMode.WFH):
        raise ValidationFailed("A presence list can only be read as Office or WFH days")
    today = local_date(clock.utcnow(), office_tz())
    table = read_workbook(filename, content, sheet)
    result = parse_rows(db, table.rows, today, presence_mode)
    parsed = result.rows
    upload = AttendanceUpload(filename=filename[:255], file_sha256=hashlib.sha256(content).hexdigest(),
                              file_size=len(content), status=UploadStatus.PREVIEW, uploaded_by_id=admin.id,
                              row_count=len(parsed), error_count=sum(1 for p in parsed if p.error))
    db.add(upload)
    db.flush()
    db.bulk_insert_mappings(AttendanceUploadRow, [{**p.as_row(), "upload_id": upload.id} for p in parsed])
    file_info = {
        "sheet": table.sheet, "sheets": table.sheets,
        "presence_list": result.presence_list,
        "presence_mode": presence_mode.value if result.presence_list else None,
        "unknown_employees": _unknown_list(result),
        "name_mismatches": result.name_mismatches[:200],
        "date_notes": _date_notes(result.dates),
        "blank_rows": result.blank_rows,
    }
    upload.summary = {**file_info, **_summary(db, parsed, result.columns, overwrite=False)}
    audit.record(db, actor_id=admin.id, action="ATTENDANCE_UPLOAD_PREVIEWED", entity_type="attendance_upload",
                 entity_id=upload.id, after={"filename": upload.filename, "sheet": table.sheet,
                                             "rows": upload.row_count, "errors": upload.error_count}, ctx=ctx)
    db.commit()
    db.refresh(upload)
    return upload


def _unique_email(db: Session, ref: str, taken: set[str]) -> str:
    base = re.sub(r"[^a-z0-9]+", ".", ref.lower()).strip(".") or "employee"
    for i in range(1000):
        email = f"{base}{'' if i == 0 else f'.{i + 1}'}@{IMPORT_EMAIL_DOMAIN}"
        if email not in taken and not db.scalar(select(User.id).where(User.email == email)):
            taken.add(email)
            return email
    raise Conflict(f"Could not find a free placeholder email for {ref!r}")


def create_employees(db: Session, admin: User, upload_id: int, refs: list[str] | None,
                     ctx: RequestContext) -> AttendanceUpload:
    """Add the unknown people of a previewed upload as employees (ID and name from the file, a placeholder
    email, no usable password, joined on the 1st of the month of their first date in the file), then re-check
    the upload's rows."""
    upload = db.execute(select(AttendanceUpload).where(AttendanceUpload.id == upload_id)
                        .with_for_update()).scalar_one_or_none()
    if upload is None:
        raise NotFound("Upload not found")
    if upload.status != UploadStatus.PREVIEW:
        raise Conflict(f"This upload is already {upload.status.value.lower()}")
    summary = dict(upload.summary or {})
    wanted = {ref_key(r) for r in refs} if refs is not None else None
    todo = [u for u in summary.get("unknown_employees", [])
            if u["can_add"] and (wanted is None or ref_key(u["employee_ref"]) in wanted)]
    if not todo:
        raise ValidationFailed("There is nobody to add from this upload")
    today = local_date(clock.utcnow(), office_tz())
    created: dict[str, int] = {}
    taken: set[str] = set()
    for u in todo:
        code = u["employee_ref"].upper()
        existing = db.scalar(select(User).where(func.upper(User.employee_code) == code))
        if existing is not None:  # added since the preview: just link the rows
            created[ref_key(u["employee_ref"])] = existing.id
            continue
        first = date.fromisoformat(u["first_date"]) if u["first_date"] else today
        name = (u["name"] or u["employee_ref"]).strip()
        user = User(employee_code=code, full_name=(name if len(name) >= 2 else u["employee_ref"])[:120],
                    email=_unique_email(db, u["employee_ref"], taken),
                    password_hash=hash_password(generate_token()),  # nobody knows it: sign-in needs a reset or SSO
                    role=Role.EMPLOYEE, is_active=True, attendance_tracked=True,
                    date_joined=min(first.replace(day=1), today))  # employed for the whole month of the file
        db.add(user)
        db.flush()
        audit.record(db, actor_id=admin.id, action="USER_CREATED", entity_type="user", entity_id=user.id,
                     after={**user_snapshot(user), "via_upload": upload.id}, ctx=ctx)
        created[ref_key(u["employee_ref"])] = user.id
    rows = list(db.scalars(select(AttendanceUploadRow).where(AttendanceUploadRow.upload_id == upload.id)))
    for r in rows:
        if r.error and r.error.startswith(UNKNOWN_PREFIX) and ref_key(r.employee_ref) in created:
            r.employee_id, r.error = created[ref_key(r.employee_ref)], None
    db.flush()
    upload.error_count = sum(1 for r in rows if r.error)
    summary["unknown_employees"] = [u for u in summary.get("unknown_employees", [])
                                    if ref_key(u["employee_ref"]) not in created]
    summary.update(_summary(db, rows, summary.get("columns", {}), overwrite=False))
    summary["employees_added"] = summary.get("employees_added", 0) + len(created)
    upload.summary = summary
    audit.record(db, actor_id=admin.id, action="ATTENDANCE_UPLOAD_EMPLOYEES_ADDED", entity_type="attendance_upload",
                 entity_id=upload.id, after={"employees": len(created), "errors": upload.error_count}, ctx=ctx)
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
        if c.cls.work_mode == WorkMode.OFFICE and c.cls.hours_known and c.cls.office_minutes < policy.min_office_minutes:
            office_below_minimum += 1
        if c.cls.conflicts:
            u = users[key[0]]
            conflicts.append({"employee_code": u.employee_code, "full_name": u.full_name, "date": key[1].isoformat(),
                              "conflicts": list(c.cls.conflicts), "rows": updates[key].rows})
    protected = [k for k in updates if existing.get(k) is not None and existing[k].override_work_mode is not None]
    conflicts.sort(key=lambda x: (x["date"], x["full_name"]))
    # Unknown-employee rows are listed separately (and can be fixed in one step): other problems come first.
    errors = sorted((r for r in rows if r.error), key=lambda r: (r.error.startswith(UNKNOWN_PREFIX), r.row_number))
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
