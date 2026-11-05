"""Link users to their automatic-attendance identities (Entra object id, Wi-Fi identity) from a CSV.

CSV header: ``employee_code`` and/or ``email``, plus ``graph_user_id`` and/or ``wifi_identity``.
A blank cell leaves the current value unchanged. The whole file is validated first; nothing is
written if any row has an error.
"""

import csv
import io
import re
import uuid
from dataclasses import dataclass, field

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core.context import RequestContext
from app.models import User
from app.services import audit

_MAC = re.compile(r"^[0-9a-f]{2}([:\-.]?[0-9a-f]{2}){5}$")


def normalize_wifi_identity(raw: str | None) -> str | None:
    """802.1X usernames are compared case-insensitively; MAC addresses become aa:bb:cc:dd:ee:ff."""
    value = (raw or "").strip().lower()
    if not value:
        return None
    if _MAC.match(value) or (len(value) == 14 and value.count(".") == 2):
        hexdigits = re.sub(r"[^0-9a-f]", "", value)
        if len(hexdigits) == 12:
            return ":".join(hexdigits[i:i + 2] for i in range(0, 12, 2))
    if len(value) > 255:
        raise ValueError("wifi_identity is longer than 255 characters")
    return value


def normalize_graph_user_id(raw: str | None) -> str | None:
    value = (raw or "").strip()
    if not value:
        return None
    try:
        return str(uuid.UUID(value))
    except ValueError:
        raise ValueError(f"graph_user_id '{value}' is not a valid Entra object id (GUID)") from None


@dataclass
class Change:
    user_id: int
    employee_code: str
    before: dict
    after: dict


@dataclass
class Plan:
    changes: list[Change] = field(default_factory=list)
    unchanged: int = 0
    errors: list[str] = field(default_factory=list)


def plan_from_csv(db: Session, text: str) -> Plan:
    plan = Plan()
    reader = csv.DictReader(io.StringIO(text.lstrip("﻿")))
    headers = {h.strip().lower() for h in (reader.fieldnames or [])}
    if not headers & {"employee_code", "email"} or not headers & {"graph_user_id", "wifi_identity"}:
        plan.errors.append("CSV needs an employee_code or email column and a graph_user_id or wifi_identity column")
        return plan

    seen: dict[tuple[str, str], int] = {}  # (field, value) -> line
    proposed: dict[int, dict] = {}
    for line_no, raw in enumerate(reader, start=2):
        row = {(k or "").strip().lower(): (v or "").strip() for k, v in raw.items()}
        code, email = row.get("employee_code", ""), row.get("email", "").lower()
        if not code and not email:
            plan.errors.append(f"line {line_no}: employee_code or email is required")
            continue
        user = db.scalar(select(User).where(func.upper(User.employee_code) == code.upper())) if code else \
            db.scalar(select(User).where(User.email == email))
        if user is None:
            plan.errors.append(f"line {line_no}: no user with {'employee_code ' + code if code else 'email ' + email}")
            continue
        if user.id in proposed:
            plan.errors.append(f"line {line_no}: {user.employee_code} appears more than once")
            continue
        values: dict[str, str] = {}
        try:
            if gid := normalize_graph_user_id(row.get("graph_user_id")):
                values["graph_user_id"] = gid
            if wid := normalize_wifi_identity(row.get("wifi_identity")):
                values["wifi_identity"] = wid
        except ValueError as exc:
            plan.errors.append(f"line {line_no}: {exc}")
            continue
        for name, value in values.items():
            if (name, value) in seen:
                plan.errors.append(f"line {line_no}: {name} {value} is also used on line {seen[(name, value)]}")
            seen[(name, value)] = line_no
        proposed[user.id] = values
        before = {"graph_user_id": user.graph_user_id, "wifi_identity": user.wifi_identity}
        after = {**before, **values}
        if after == before:
            plan.unchanged += 1
        else:
            plan.changes.append(Change(user.id, user.employee_code, before, after))

    # Values must not already belong to someone else (unless that person is reassigned in this file).
    for change in plan.changes:
        for name in ("graph_user_id", "wifi_identity"):
            value = change.after[name]
            if value is None or value == change.before[name]:
                continue
            owner = db.scalar(select(User).where(getattr(User, name) == value, User.id != change.user_id))
            if owner is not None and proposed.get(owner.id, {}).get(name, getattr(owner, name)) == value:
                plan.errors.append(f"{name} {value} already belongs to {owner.employee_code}")
    return plan


def apply_plan(db: Session, plan: Plan, actor_id: int | None, ctx: RequestContext) -> int:
    if plan.errors:
        raise ValueError("Refusing to apply a plan with errors")
    # Clear values that move between people first, so the unique indexes never see a duplicate.
    for change in plan.changes:
        user = db.get(User, change.user_id)
        for name in ("graph_user_id", "wifi_identity"):
            if change.after[name] != change.before[name]:
                setattr(user, name, None)
    db.flush()
    for change in plan.changes:
        user = db.get(User, change.user_id)
        user.graph_user_id = change.after["graph_user_id"]
        user.wifi_identity = change.after["wifi_identity"]
        b, a = audit.changes(change.before, change.after)
        audit.record(db, actor_id=actor_id, action="USER_IDENTITY_LINKED", entity_type="user", entity_id=user.id,
                     before=b, after=a, ctx=ctx)
    db.commit()
    return len(plan.changes)
