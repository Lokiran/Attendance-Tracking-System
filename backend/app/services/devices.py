"""Office sites and the device register.

The office Wi-Fi uses one shared password, so its logs show devices (MAC addresses), never people. This
register says whose device each one is. The company tracks laptops by **serial number**, so that is the key:

* an asset list fills serial number → employee;
* an Intune export (Devices → Export: "Serial number", "Wi-Fi MAC", "Primary user UPN") or a script on the
  laptops fills serial number → MAC address.

Either file can be imported first; rows are matched to existing devices by serial number (then by MAC
address), and a blank cell never clears a value that is already there. An import is checked first (dry run)
and only written when applied.
"""

from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass, field

from sqlalchemy import func, or_, select
from sqlalchemy.orm import Session

from app.core.context import RequestContext
from app.core.errors import Conflict, NotFound, ValidationFailed
from app.core.wfo_mapping import normalize, normalize_header
from app.models import Device, OfficeSite, User
from app.services import audit
from app.services.timesheet_files import (
    ACCEPTED_TEXT,
    MAX_UPLOAD_BYTES,
    _csv_rows,
    _xls_sheets,
    _xlsx_sheets,
    cell_text,
)

HEADER_SEARCH_ROWS = 20
MAX_ROWS = 5000

# ------------------------------------------------------------------ identifiers

_MAC_SEPARATORS = re.compile(r"[\s:.\-]")
# Serial numbers some laptops report when the maker did not set one: never a real asset.
_JUNK_SERIALS = {"", "NA", "N/A", "NONE", "NULL", "-", "0", "TO BE FILLED BY O.E.M.", "DEFAULT STRING",
                 "SYSTEM SERIAL NUMBER", "INVALID"}


def normalize_mac(raw: str | None) -> str | None:
    """"AA-BB-CC-DD-EE-FF", "aabb.ccdd.eeff", "AABBCCDDEEFF" → "aa:bb:cc:dd:ee:ff". Blank → None; anything
    else that is not 12 hex digits raises ValueError."""
    text = cell_text(raw)
    if not text:
        return None
    digits = _MAC_SEPARATORS.sub("", text).lower()
    if not re.fullmatch(r"[0-9a-f]{12}", digits) or digits in ("000000000000", "ffffffffffff"):
        raise ValueError(f"{text!r} is not a MAC address (12 hex digits, like AA:BB:CC:DD:EE:FF)")
    return ":".join(digits[i:i + 2] for i in range(0, 12, 2))


def is_random_mac(mac: str | None) -> bool:
    """A "private" / random Wi-Fi address (locally administered bit set): it can change and does not identify
    a laptop. On Windows: Settings → Wi-Fi → Random hardware addresses (or an Intune Wi-Fi profile)."""
    return bool(mac) and bool(int(mac[1], 16) & 2)


def normalize_serial(raw: str | None) -> str | None:
    text = " ".join(cell_text(raw).split()).upper()
    return None if text in _JUNK_SERIALS else text[:64]


def _clean(raw, limit: int) -> str | None:
    text = " ".join(cell_text(raw).split())
    return text[:limit] or None


# ------------------------------------------------------------------ office sites

SITE_FIELDS = ("name", "code", "network_name", "address", "is_active")


def _site_snapshot(s: OfficeSite) -> dict:
    return {f: getattr(s, f) for f in SITE_FIELDS}


def _check_site_unique(db: Session, name: str | None, code: str | None, exclude: int | None = None) -> None:
    for col, value, label in ((OfficeSite.name, name, "name"), (OfficeSite.code, code, "code")):
        if value is None:
            continue
        stmt = select(OfficeSite.id).where(func.lower(col) == value.lower())
        if exclude is not None:
            stmt = stmt.where(OfficeSite.id != exclude)
        if db.scalar(stmt) is not None:
            raise Conflict(f"Another office already uses the {label} {value!r}")


def create_site(db: Session, actor: User, data: dict, ctx: RequestContext) -> OfficeSite:
    name, code = data["name"].strip(), data["code"].strip().upper()
    _check_site_unique(db, name, code)
    site = OfficeSite(name=name, code=code, network_name=_clean(data.get("network_name"), 64),
                      address=(data.get("address") or "").strip() or None, is_active=True)
    db.add(site)
    db.flush()
    audit.record(db, actor_id=actor.id, action="OFFICE_SITE_CREATED", entity_type="office_site", entity_id=site.id,
                 after=_site_snapshot(site), ctx=ctx)
    db.commit()
    return site


def update_site(db: Session, actor: User, site_id: int, data: dict, ctx: RequestContext) -> OfficeSite:
    site = db.get(OfficeSite, site_id)
    if site is None:
        raise NotFound("Office not found")
    before = _site_snapshot(site)
    if "name" in data and data["name"] is not None:
        data["name"] = data["name"].strip()
    if "code" in data and data["code"] is not None:
        data["code"] = data["code"].strip().upper()
    _check_site_unique(db, data.get("name"), data.get("code"), exclude=site.id)
    for f in SITE_FIELDS:
        if f not in data:
            continue
        if f in ("name", "code", "is_active") and data[f] is None:
            continue  # required fields are never cleared
        value = data[f]
        if f == "network_name":
            value = _clean(value, 64)
        elif f == "address":
            value = (value or "").strip() or None
        setattr(site, f, value)
    db.flush()
    b, a = audit.changes(before, _site_snapshot(site))
    if a:
        audit.record(db, actor_id=actor.id, action="OFFICE_SITE_UPDATED", entity_type="office_site",
                     entity_id=site.id, before=b, after=a, ctx=ctx)
    db.commit()
    return site


# ------------------------------------------------------------------ devices

DEVICE_FIELDS = ("serial_number", "asset_tag", "mac_address", "hostname", "model", "user_id", "is_active", "notes")


def _snapshot(d: Device) -> dict:
    return {f: getattr(d, f) for f in DEVICE_FIELDS}


def _normalized(data: dict) -> dict:
    """Request fields → stored form. Raises ValidationFailed for a bad MAC address."""
    out = dict(data)
    try:
        if "mac_address" in out:
            out["mac_address"] = normalize_mac(out["mac_address"])
    except ValueError as exc:
        raise ValidationFailed(str(exc)) from None
    if "serial_number" in out:
        out["serial_number"] = normalize_serial(out["serial_number"])
    for f, limit in (("asset_tag", 64), ("hostname", 64), ("model", 120)):
        if f in out:
            out[f] = _clean(out[f], limit)
    if "notes" in out:
        out["notes"] = (out["notes"] or "").strip() or None
    return out


def _check_device_unique(db: Session, serial: str | None, mac: str | None, exclude: int | None = None) -> None:
    for col, value, label in ((Device.serial_number, serial, "serial number"), (Device.mac_address, mac, "MAC address")):
        if not value:
            continue
        stmt = select(Device).where(col == value)
        if exclude is not None:
            stmt = stmt.where(Device.id != exclude)
        other = db.scalar(stmt)
        if other is not None:
            owner = f" ({other.user.full_name})" if other.user else ""
            raise Conflict(f"The {label} {value} is already registered to another device{owner}")


def _check_owner(db: Session, user_id: int | None) -> None:
    if user_id is not None and db.get(User, user_id) is None:
        raise ValidationFailed("Employee does not exist")


def create_device(db: Session, actor: User, data: dict, ctx: RequestContext) -> Device:
    data = _normalized(data)
    if not data.get("serial_number") and not data.get("mac_address"):
        raise ValidationFailed("Enter the serial number, the MAC address, or both")
    _check_owner(db, data.get("user_id"))
    _check_device_unique(db, data.get("serial_number"), data.get("mac_address"))
    device = Device(**{f: data.get(f) for f in DEVICE_FIELDS if f != "is_active"}, is_active=True, source="MANUAL")
    db.add(device)
    db.flush()
    audit.record(db, actor_id=actor.id, action="DEVICE_CREATED", entity_type="device", entity_id=device.id,
                 after=_snapshot(device), ctx=ctx)
    db.commit()
    return device


def update_device(db: Session, actor: User, device_id: int, data: dict, ctx: RequestContext) -> Device:
    device = db.get(Device, device_id)
    if device is None:
        raise NotFound("Device not found")
    data = _normalized(data)
    serial = data["serial_number"] if "serial_number" in data else device.serial_number
    mac = data["mac_address"] if "mac_address" in data else device.mac_address
    if not serial and not mac:
        raise ValidationFailed("A device needs a serial number or a MAC address")
    if "user_id" in data:
        _check_owner(db, data["user_id"])
    _check_device_unique(db, data.get("serial_number"), data.get("mac_address"), exclude=device.id)
    before = _snapshot(device)
    for f in DEVICE_FIELDS:
        if f in data and not (f == "is_active" and data[f] is None):
            setattr(device, f, data[f])
    db.flush()
    b, a = audit.changes(before, _snapshot(device))
    if a:
        audit.record(db, actor_id=actor.id, action="DEVICE_UPDATED", entity_type="device", entity_id=device.id,
                     before=b, after=a, ctx=ctx)
    db.commit()
    return device


def list_devices(db: Session, *, search: str | None, assigned: bool | None, has_mac: bool | None,
                 active: bool | None, offset: int, limit: int) -> tuple[list[Device], int]:
    stmt = select(Device).outerjoin(User, Device.user_id == User.id)
    if search:
        like = f"%{search.strip().lower()}%"
        mac_like = "%" + _MAC_SEPARATORS.sub("", search.strip().lower()) + "%"
        stmt = stmt.where(or_(
            func.lower(Device.serial_number).like(like), func.lower(Device.asset_tag).like(like),
            func.lower(Device.hostname).like(like), func.lower(Device.model).like(like),
            func.replace(Device.mac_address, ":", "").like(mac_like),
            func.lower(User.full_name).like(like), func.lower(User.employee_code).like(like)))
    if assigned is not None:
        stmt = stmt.where(Device.user_id.is_not(None) if assigned else Device.user_id.is_(None))
    if has_mac is not None:
        stmt = stmt.where(Device.mac_address.is_not(None) if has_mac else Device.mac_address.is_(None))
    if active is not None:
        stmt = stmt.where(Device.is_active.is_(active))
    total = db.scalar(select(func.count()).select_from(stmt.subquery())) or 0
    rows = list(db.scalars(stmt.order_by(User.full_name.is_(None), User.full_name, Device.serial_number)
                           .offset(offset).limit(limit)).unique())
    return rows, total


def register_summary(db: Session) -> dict:
    """Counts for the register's header: how ready it is for Wi-Fi matching."""
    active = select(Device).where(Device.is_active.is_(True)).subquery()
    total = db.scalar(select(func.count()).select_from(active)) or 0
    with_mac = db.scalar(select(func.count()).select_from(active).where(active.c.mac_address.is_not(None))) or 0
    unassigned = db.scalar(select(func.count()).select_from(active).where(active.c.user_id.is_(None))) or 0
    macs = db.scalars(select(Device.mac_address).where(Device.is_active.is_(True), Device.mac_address.is_not(None)))
    random_macs = sum(1 for m in macs if is_random_mac(m))
    tracked = select(User.id).where(User.is_active.is_(True), User.attendance_tracked.is_(True)).subquery()
    people = db.scalar(select(func.count()).select_from(tracked)) or 0
    covered = db.scalar(select(func.count(func.distinct(Device.user_id))).where(
        Device.is_active.is_(True), Device.mac_address.is_not(None), Device.user_id.in_(select(tracked.c.id)))) or 0
    return {"devices": total, "with_mac": with_mac, "without_mac": total - with_mac, "unassigned": unassigned,
            "random_mac": random_macs, "people_tracked": people, "people_with_device": covered,
            "sites": db.scalar(select(func.count(OfficeSite.id)).where(OfficeSite.is_active.is_(True))) or 0}


# ------------------------------------------------------------------ asset list import

ASSET_COLUMNS: dict[str, tuple[str, ...]] = {
    "serial": ("serial", "serial number", "serial no", "serial num", "serialnumber", "sn", "s n", "device serial",
               "device serial number", "asset serial", "asset serial number", "service tag"),
    "asset_tag": ("asset tag", "asset id", "asset no", "asset number", "asset code", "tag"),
    "employee": ("employee id", "emp id", "employee code", "emp code", "emp no", "employee no", "employee number",
                 "code", "staff id", "associate id"),
    "email": ("email", "e mail", "user email", "email address", "primary user upn", "primary user email address",
              "primary user email", "user principal name", "upn", "assigned user email"),
    "name": ("employee name", "emp name", "name", "user", "user name", "assigned to", "assigned user", "owner",
             "primary user display name", "full name", "employee"),
    "mac": ("wi fi mac", "wifi mac", "wi fi mac address", "wifi mac address", "wireless mac", "wlan mac", "mac",
            "mac address", "mac id"),
    "hostname": ("device name", "hostname", "host name", "computer name", "machine name", "laptop name"),
    "model": ("model", "device model", "laptop model"),
}
_ALIAS = {normalize_header(a): key for key, aliases in ASSET_COLUMNS.items() for a in aliases}


def _read_rows(filename: str, content: bytes) -> tuple[list[list], str | None]:
    name = filename.lower()
    if len(content) > MAX_UPLOAD_BYTES:
        raise ValidationFailed(f"The file is larger than {MAX_UPLOAD_BYTES // (1024 * 1024)} MB")
    if not content:
        raise ValidationFailed("The file is empty")
    if name.endswith((".csv", ".tsv", ".txt")):
        return _csv_rows(content), None
    if name.endswith((".xlsx", ".xlsm", ".xls")):
        sheets = _xlsx_sheets(content) if not name.endswith(".xls") else _xls_sheets(content)
        for sheet, rows in sheets.items():
            if _find_header(rows) is not None:
                return rows, sheet
        first = next(iter(sheets.values()), [])
        return first, next(iter(sheets), None)
    raise ValidationFailed(f"Upload the device list as {ACCEPTED_TEXT.replace(' or PDF', '')}")


def _find_header(rows: list[list]) -> tuple[int, dict[str, int]] | None:
    for i, row in enumerate(rows[:HEADER_SEARCH_ROWS]):
        cols: dict[str, int] = {}
        for j, h in enumerate(row):
            key = _ALIAS.get(normalize_header(cell_text(h)))
            if key and key not in cols:
                cols[key] = j
        if ({"serial", "mac"} & cols.keys()) and ({"employee", "email", "name", "mac"} & cols.keys()):
            return i, cols
    return None


@dataclass
class _People:
    by_code: dict[str, User]
    by_email: dict[str, User]
    by_name: dict[str, list[User]]

    @classmethod
    def load(cls, db: Session) -> "_People":
        users = list(db.scalars(select(User)).unique())
        by_name: dict[str, list[User]] = {}
        for u in users:
            by_name.setdefault(normalize(u.full_name), []).append(u)
        return cls({" ".join(u.employee_code.split()).lower(): u for u in users},
                   {u.email.lower(): u for u in users}, by_name)

    def find(self, employee: str | None, email: str | None, name: str | None) -> tuple[User | None, str | None]:
        """(the person, how they were written) — None when nobody matches or a name is shared."""
        for ref in (employee, email):
            if not ref:
                continue
            key = " ".join(ref.split()).lower()
            hit = self.by_email.get(key) if "@" in key else self.by_code.get(key)
            if hit is not None:
                return hit, ref
        if name:
            same = self.by_name.get(normalize(name), [])
            if len(same) == 1:
                return same[0], name
        return None, employee or email or name


@dataclass
class _Row:
    row: int
    serial: str | None = None
    mac: str | None = None
    hostname: str | None = None
    model: str | None = None
    asset_tag: str | None = None
    owner_written: str | None = None
    owner: User | None = None
    action: str = "unchanged"            # create | update | unchanged | error
    changes: dict = field(default_factory=dict)
    problems: list[str] = field(default_factory=list)
    device: Device | None = None


def _plan(db: Session, rows: list[list]) -> tuple[list[_Row], dict[str, str], int]:
    found = _find_header(rows)
    if found is None:
        first = next((r for r in rows if any(cell_text(c) for c in r)), [])
        raise ValidationFailed("Could not find a serial number or MAC address column, and an employee column, in "
                               "the first rows. Found: " + (", ".join(cell_text(h) for h in first if cell_text(h))
                                                            or "nothing"))
    at, cols = found
    body = rows[at + 1:]
    if len(body) > MAX_ROWS:
        raise ValidationFailed(f"At most {MAX_ROWS} rows per file")
    people = _People.load(db)
    by_serial = {d.serial_number: d for d in db.scalars(select(Device).where(Device.serial_number.is_not(None)))}
    by_mac = {d.mac_address: d for d in db.scalars(select(Device).where(Device.mac_address.is_not(None)))}
    seen_serial: dict[str, int] = {}
    seen_mac: dict[str, int] = {}
    out: list[_Row] = []
    blank = 0

    def cell(r: list, key: str):
        i = cols.get(key)
        return r[i] if i is not None and i < len(r) else None

    for n, r in enumerate(body, start=at + 2):
        if not any(cell_text(cell(r, k)) for k in cols):
            blank += 1
            continue
        p = _Row(row=n, serial=normalize_serial(cell(r, "serial")), hostname=_clean(cell(r, "hostname"), 64),
                 model=_clean(cell(r, "model"), 120), asset_tag=_clean(cell(r, "asset_tag"), 64))
        out.append(p)
        try:
            p.mac = normalize_mac(cell(r, "mac"))
        except ValueError as exc:
            p.action, p.problems = "error", [str(exc)]
            continue
        if not p.serial and not p.mac:
            p.action, p.problems = "error", ["no serial number or MAC address"]
            continue
        if p.serial and p.serial in seen_serial:
            p.action, p.problems = "error", [f"serial number repeated (first on row {seen_serial[p.serial]})"]
            continue
        if p.mac and p.mac in seen_mac:
            p.action, p.problems = "error", [f"MAC address repeated (first on row {seen_mac[p.mac]})"]
            continue
        employee, email, name = (_clean(cell(r, k), 255) for k in ("employee", "email", "name"))
        p.owner, p.owner_written = people.find(employee, email, name)
        if p.owner is None and p.owner_written:
            p.problems.append(f"no employee matches {p.owner_written!r}: the device is kept unassigned")
        if is_random_mac(p.mac):
            p.problems.append("private (random) Wi-Fi address: it can change and will not identify the laptop; "
                              "turn off random hardware addresses and read the address again")
        existing = by_serial.get(p.serial) if p.serial else None
        if existing is None and p.mac:
            existing = by_mac.get(p.mac)
            if existing is not None and existing.serial_number and p.serial and existing.serial_number != p.serial:
                p.action, p.problems = "error", [f"MAC address already belongs to serial {existing.serial_number}"]
                continue
        if p.mac and existing is not None and by_mac.get(p.mac) not in (None, existing):
            other = by_mac[p.mac]
            p.action, p.problems = "error", [f"MAC address already belongs to serial {other.serial_number or other.id}"]
            continue
        if p.serial:
            seen_serial[p.serial] = n
        if p.mac:
            seen_mac[p.mac] = n
        p.device = existing
        if existing is None:
            p.action = "create"
            continue
        wanted = {"serial_number": p.serial, "mac_address": p.mac, "hostname": p.hostname, "model": p.model,
                  "asset_tag": p.asset_tag, "user_id": p.owner.id if p.owner else None}
        for f, value in wanted.items():
            if value is not None and getattr(existing, f) != value:  # a blank cell never clears a value
                p.changes[f] = value
        if "user_id" in p.changes and existing.user_id is not None:
            was = existing.user.full_name if existing.user else existing.user_id
            p.problems.append(f"owner changes from {was} to {p.owner.full_name}")
        p.action = "update" if p.changes else "unchanged"
    columns = {k: cell_text(rows[at][i]) for k, i in cols.items()}
    return out, columns, blank


def _result(plan: list[_Row], columns: dict[str, str], blank: int, sheet: str | None, applied: bool) -> dict:
    counts = Counter(p.action for p in plan)
    unknown = Counter(p.owner_written for p in plan if p.owner is None and p.owner_written and p.action != "error")
    return {
        "applied": applied, "sheet": sheet, "columns": columns, "rows": len(plan), "blank_rows": blank,
        "created": counts["create"], "updated": counts["update"], "unchanged": counts["unchanged"],
        "errors": counts["error"],
        "with_mac": sum(1 for p in plan if p.action != "error" and (p.mac or (p.device and p.device.mac_address))),
        "random_mac": sum(1 for p in plan if p.action != "error" and is_random_mac(p.mac)),
        "unknown_owners": [{"written": k, "rows": v} for k, v in unknown.most_common(200)],
        "items": [{"row": p.row, "action": p.action, "serial_number": p.serial, "mac_address": p.mac,
                   "hostname": p.hostname, "owner": p.owner.full_name if p.owner else None,
                   "owner_code": p.owner.employee_code if p.owner else None, "owner_written": p.owner_written,
                   "changes": sorted(p.changes), "problems": p.problems}
                  for p in sorted(plan, key=lambda p: (p.action != "error", not p.problems, p.row))][:500],
    }


def import_devices(db: Session, actor: User, filename: str, content: bytes, apply: bool,
                   ctx: RequestContext) -> dict:
    """Check (apply=False) or write (apply=True) an asset list / Intune export."""
    rows, sheet = _read_rows(filename, content)
    plan, columns, blank = _plan(db, rows)
    if not apply:
        return _result(plan, columns, blank, sheet, applied=False)
    for p in plan:
        if p.action == "create":
            device = Device(serial_number=p.serial, mac_address=p.mac, hostname=p.hostname, model=p.model,
                            asset_tag=p.asset_tag, user_id=p.owner.id if p.owner else None, is_active=True,
                            source="IMPORT")
            db.add(device)
            p.device = device
        elif p.action == "update":
            for f, value in p.changes.items():
                setattr(p.device, f, value)
    db.flush()
    result = _result(plan, columns, blank, sheet, applied=True)
    audit.record(db, actor_id=actor.id, action="DEVICES_IMPORTED", entity_type="device", entity_id=None,
                 after={"filename": filename[:255], **{k: result[k] for k in ("rows", "created", "updated",
                                                                               "unchanged", "errors")}}, ctx=ctx)
    db.commit()
    return result
