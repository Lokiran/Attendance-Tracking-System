"""Automatic attendance settings: one source of truth for every rule the jobs use."""

from dataclasses import dataclass

from sqlalchemy.orm import Session

from app.core.context import RequestContext
from app.core.errors import ValidationFailed
from app.models import AttendancePolicy, User
from app.services import audit
from app.services.policy import get_policy

FIELDS = ("auto_attendance_enabled", "auto_shadow_mode", "gap_minutes", "full_day_minutes", "half_day_minutes",
          "wifi_poll_minutes", "presence_poll_minutes", "no_data_alert_minutes", "raw_retention_days",
          "wfh_presence_states")


@dataclass(frozen=True)
class AutoRules:
    """Plain, immutable copy of the rules for jobs and the pure rules engine (no database access)."""
    gap_minutes: int
    full_day_minutes: int
    half_day_minutes: int
    wifi_poll_minutes: int
    presence_poll_minutes: int
    no_data_alert_minutes: int
    raw_retention_days: int
    wfh_presence_states: frozenset[str]


def rules(policy: AttendancePolicy) -> AutoRules:
    return AutoRules(
        gap_minutes=policy.gap_minutes, full_day_minutes=policy.full_day_minutes,
        half_day_minutes=policy.half_day_minutes, wifi_poll_minutes=policy.wifi_poll_minutes,
        presence_poll_minutes=policy.presence_poll_minutes, no_data_alert_minutes=policy.no_data_alert_minutes,
        raw_retention_days=policy.raw_retention_days, wfh_presence_states=frozenset(policy.wfh_presence_states),
    )


def update(db: Session, actor: User, data: dict, ctx: RequestContext) -> AttendancePolicy:
    policy = get_policy(db)
    before = {f: getattr(policy, f) for f in FIELDS}
    merged = {**before, **{k: v for k, v in data.items() if k in FIELDS and v is not None}}
    # Checked on the merged values, so a partial update cannot break the rule.
    if merged["half_day_minutes"] > merged["full_day_minutes"]:
        raise ValidationFailed("Half-day minutes cannot be more than full-day minutes")
    for f in FIELDS:
        setattr(policy, f, merged[f])
    policy.updated_by_id = actor.id
    db.flush()
    b, a = audit.changes(before, {f: getattr(policy, f) for f in FIELDS})
    if a:
        audit.record(db, actor_id=actor.id, action="AUTO_ATTENDANCE_SETTINGS_UPDATED", entity_type="attendance_policy",
                     entity_id=1, before=b, after=a, ctx=ctx)
    db.commit()
    db.refresh(policy)
    return policy
