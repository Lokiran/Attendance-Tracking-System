"""Audit trail helper. Callers must never pass credentials or secrets in before/after values."""

from datetime import date, datetime, time
from enum import Enum
from typing import Any

from sqlalchemy.orm import Session

from app.core.context import RequestContext
from app.models import AuditLog

_FORBIDDEN_KEYS = {"password", "password_hash", "token", "token_hash", "csrf_token", "secret"}


def _clean(value: Any) -> Any:
    if isinstance(value, dict):
        return {k: _clean(v) for k, v in value.items() if k not in _FORBIDDEN_KEYS}
    if isinstance(value, (list, tuple, set)):
        return [_clean(v) for v in value]
    if isinstance(value, Enum):
        return value.value
    if isinstance(value, (datetime, date, time)):
        return value.isoformat()
    return value


def record(
    db: Session,
    *,
    actor_id: int | None,
    action: str,
    entity_type: str,
    entity_id: Any = None,
    before: dict | None = None,
    after: dict | None = None,
    ctx: RequestContext | None = None,
) -> AuditLog:
    entry = AuditLog(
        actor_user_id=actor_id,
        action=action,
        entity_type=entity_type,
        entity_id=None if entity_id is None else str(entity_id),
        before_values=_clean(before) if before is not None else None,
        after_values=_clean(after) if after is not None else None,
        request_id=ctx.request_id if ctx else None,
        ip_address=ctx.ip_address if ctx else None,
    )
    db.add(entry)
    return entry


def changes(before: dict, after: dict) -> tuple[dict, dict]:
    """Reduce two snapshots to only the keys that changed."""
    keys = [k for k in after if before.get(k) != after.get(k)]
    return {k: before.get(k) for k in keys}, {k: after.get(k) for k in keys}
