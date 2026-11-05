"""Work schedules, holidays and the attendance policy."""

from datetime import date

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.context import RequestContext
from app.core.errors import Conflict, NotFound, ValidationFailed
from app.models import Department, Holiday, User, WorkSchedule
from app.services import audit
from app.services.policy import get_policy

POLICY_FIELDS = ("min_present_minutes", "count_incomplete_as_present", "max_session_minutes",
                 "late_grace_minutes", "correction_window_days", "manager_can_approve", "notes")


def schedule_snapshot(s: WorkSchedule) -> dict:
    return {"name": s.name, "employee_id": s.employee_id, "department_id": s.department_id,
            "start_time": s.start_time, "end_time": s.end_time, "required_minutes": s.required_minutes,
            "working_days": s.working_days, "effective_from": s.effective_from, "effective_to": s.effective_to}


def _validate_schedule(db: Session, data: dict, schedule_id: int | None) -> None:
    if data.get("employee_id") is not None and data.get("department_id") is not None:
        raise ValidationFailed("A schedule applies to an employee, a department, or the whole organisation")
    if data.get("employee_id") is not None and db.get(User, data["employee_id"]) is None:
        raise ValidationFailed("Employee does not exist")
    if data.get("department_id") is not None and db.get(Department, data["department_id"]) is None:
        raise ValidationFailed("Department does not exist")
    days = data["working_days"]
    if not days or any(d not in range(1, 8) for d in days) or len(set(days)) != len(days):
        raise ValidationFailed("Working days must be distinct ISO weekday numbers between 1 (Mon) and 7 (Sun)")
    if not 1 <= data["required_minutes"] <= 1440:
        raise ValidationFailed("Required minutes must be between 1 and 1440")
    if data["start_time"] == data["end_time"]:
        raise ValidationFailed("Start and end time cannot be equal")
    eff_to: date | None = data.get("effective_to")
    if eff_to is not None and eff_to < data["effective_from"]:
        raise ValidationFailed("Effective end date must be on or after the start date")

    # Two assignments for the same target must not be effective at the same time.
    stmt = select(WorkSchedule).where(
        WorkSchedule.employee_id.is_(None) if data.get("employee_id") is None
        else WorkSchedule.employee_id == data["employee_id"],
        WorkSchedule.department_id.is_(None) if data.get("department_id") is None
        else WorkSchedule.department_id == data["department_id"],
    )
    for other in db.scalars(stmt):
        if other.id == schedule_id:
            continue
        other_end = other.effective_to or date.max
        if other.effective_from <= (eff_to or date.max) and data["effective_from"] <= other_end:
            raise Conflict(f"Overlaps the effective dates of schedule '{other.name}'")


def create_schedule(db: Session, actor: User, data: dict, ctx: RequestContext) -> WorkSchedule:
    data["working_days"] = sorted(data["working_days"])
    _validate_schedule(db, data, None)
    s = WorkSchedule(**data)
    db.add(s)
    db.flush()
    audit.record(db, actor_id=actor.id, action="SCHEDULE_CREATED", entity_type="work_schedule", entity_id=s.id,
                 after=schedule_snapshot(s), ctx=ctx)
    db.commit()
    return s


def update_schedule(db: Session, actor: User, schedule_id: int, data: dict, ctx: RequestContext) -> WorkSchedule:
    s = db.get(WorkSchedule, schedule_id)
    if s is None:
        raise NotFound("Schedule not found")
    before = schedule_snapshot(s)
    merged = {**before, **data}
    merged["working_days"] = sorted(merged["working_days"])
    _validate_schedule(db, merged, s.id)
    for k, v in merged.items():
        setattr(s, k, v)
    db.flush()
    b, a = audit.changes(before, schedule_snapshot(s))
    audit.record(db, actor_id=actor.id, action="SCHEDULE_UPDATED", entity_type="work_schedule", entity_id=s.id,
                 before=b, after=a, ctx=ctx)
    db.commit()
    return s


def delete_schedule(db: Session, actor: User, schedule_id: int, ctx: RequestContext) -> None:
    s = db.get(WorkSchedule, schedule_id)
    if s is None:
        raise NotFound("Schedule not found")
    audit.record(db, actor_id=actor.id, action="SCHEDULE_DELETED", entity_type="work_schedule", entity_id=s.id,
                 before=schedule_snapshot(s), ctx=ctx)
    db.delete(s)
    db.commit()


def create_holiday(db: Session, actor: User, data: dict, ctx: RequestContext) -> Holiday:
    if data.get("department_id") is not None and db.get(Department, data["department_id"]) is None:
        raise ValidationFailed("Department does not exist")
    clash = db.scalar(select(Holiday.id).where(
        Holiday.holiday_date == data["holiday_date"],
        Holiday.department_id.is_(None) if data.get("department_id") is None
        else Holiday.department_id == data["department_id"]))
    if clash is not None:
        raise Conflict("A holiday already exists on this date for the same scope")
    h = Holiday(name=data["name"].strip(), holiday_date=data["holiday_date"], department_id=data.get("department_id"))
    db.add(h)
    db.flush()
    audit.record(db, actor_id=actor.id, action="HOLIDAY_CREATED", entity_type="holiday", entity_id=h.id,
                 after={"name": h.name, "holiday_date": h.holiday_date, "department_id": h.department_id}, ctx=ctx)
    db.commit()
    return h


def delete_holiday(db: Session, actor: User, holiday_id: int, ctx: RequestContext) -> None:
    h = db.get(Holiday, holiday_id)
    if h is None:
        raise NotFound("Holiday not found")
    audit.record(db, actor_id=actor.id, action="HOLIDAY_DELETED", entity_type="holiday", entity_id=h.id,
                 before={"name": h.name, "holiday_date": h.holiday_date, "department_id": h.department_id}, ctx=ctx)
    db.delete(h)
    db.commit()


def update_policy(db: Session, actor: User, data: dict, ctx: RequestContext):
    policy = get_policy(db)
    before = {f: getattr(policy, f) for f in POLICY_FIELDS}
    for f in POLICY_FIELDS:
        if f in data and data[f] is not None:
            setattr(policy, f, data[f])
    policy.updated_by_id = actor.id
    db.flush()
    b, a = audit.changes(before, {f: getattr(policy, f) for f in POLICY_FIELDS})
    if a:
        audit.record(db, actor_id=actor.id, action="POLICY_UPDATED", entity_type="attendance_policy", entity_id=1,
                     before=b, after=a, ctx=ctx)
    db.commit()
    return policy
