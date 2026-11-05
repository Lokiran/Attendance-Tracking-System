"""Employee and department administration with hierarchy and privilege safeguards."""

from sqlalchemy import func, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.core.context import RequestContext
from app.core.errors import Conflict, NotFound, ValidationFailed
from app.core.security import hash_password, validate_password_strength
from app.core.timeutil import today_local
from app.models import Department, Role, User
from app.services import audit
from app.services.auth_service import revoke_all_sessions
from app.services.scope import manager_chain_ids


def user_snapshot(u: User) -> dict:
    return {
        "employee_code": u.employee_code, "full_name": u.full_name, "email": u.email, "role": u.role,
        "job_title": u.job_title, "department_id": u.department_id, "manager_id": u.manager_id,
        "is_active": u.is_active, "attendance_tracked": u.attendance_tracked, "date_joined": u.date_joined,
    }


def _active_admin_count(db: Session) -> int:
    return db.scalar(select(func.count()).select_from(User).where(User.role == Role.ADMIN, User.is_active.is_(True)))


def _validate_manager(db: Session, user_id: int | None, manager_id: int | None) -> None:
    if manager_id is None:
        return
    if user_id is not None and manager_id == user_id:
        raise ValidationFailed("An employee cannot be their own manager")
    manager = db.get(User, manager_id)
    if manager is None:
        raise ValidationFailed("Selected manager does not exist")
    if not manager.is_active:
        raise ValidationFailed("Selected manager is inactive")
    if manager.role not in (Role.MANAGER, Role.ADMIN):
        raise ValidationFailed("Selected manager must have the MANAGER or ADMIN role")
    if user_id is not None and user_id in manager_chain_ids(db, manager_id):
        raise ValidationFailed("This assignment would create a circular reporting relationship")


def _validate_department(db: Session, department_id: int | None) -> None:
    if department_id is not None and db.get(Department, department_id) is None:
        raise ValidationFailed("Selected department does not exist")


def _direct_report_count(db: Session, user_id: int) -> int:
    return db.scalar(select(func.count()).select_from(User).where(User.manager_id == user_id,
                                                                  User.is_active.is_(True)))


def _commit_unique(db: Session) -> None:
    try:
        db.commit()
    except IntegrityError as exc:
        db.rollback()
        msg = str(exc.orig).lower()
        if "email" in msg:
            raise Conflict("A user with this email already exists") from None
        if "employee_code" in msg:
            raise Conflict("A user with this employee ID already exists") from None
        raise Conflict("The change conflicts with existing data") from None


def create_user(db: Session, actor: User, data: dict, ctx: RequestContext) -> User:
    email = data["email"].strip().lower()
    code = data["employee_code"].strip().upper()
    if db.scalar(select(User.id).where(User.email == email)):
        raise Conflict("A user with this email already exists")
    if db.scalar(select(User.id).where(func.upper(User.employee_code) == code)):
        raise Conflict("A user with this employee ID already exists")
    validate_password_strength(data["password"])
    _validate_department(db, data.get("department_id"))
    _validate_manager(db, None, data.get("manager_id"))
    user = User(
        employee_code=code, full_name=data["full_name"].strip(), email=email,
        password_hash=hash_password(data["password"]), role=data["role"], job_title=data.get("job_title"),
        department_id=data.get("department_id"), manager_id=data.get("manager_id"), is_active=True,
        attendance_tracked=data.get("attendance_tracked", True),
        date_joined=data.get("date_joined") or today_local(),
    )
    db.add(user)
    db.flush()
    audit.record(db, actor_id=actor.id, action="USER_CREATED", entity_type="user", entity_id=user.id,
                 after=user_snapshot(user), ctx=ctx)
    _commit_unique(db)
    db.refresh(user)
    return user


def update_user(db: Session, actor: User, user_id: int, changes: dict, ctx: RequestContext) -> User:
    user = db.get(User, user_id)
    if user is None:
        raise NotFound("User not found")
    before = user_snapshot(user)

    if "email" in changes and changes["email"] is not None:
        email = changes["email"].strip().lower()
        if email != user.email and db.scalar(select(User.id).where(User.email == email)):
            raise Conflict("A user with this email already exists")
        user.email = email
    if "employee_code" in changes and changes["employee_code"] is not None:
        code = changes["employee_code"].strip().upper()
        if code != user.employee_code and db.scalar(
                select(User.id).where(func.upper(User.employee_code) == code, User.id != user.id)):
            raise Conflict("A user with this employee ID already exists")
        user.employee_code = code
    for field in ("full_name", "job_title", "date_joined", "attendance_tracked"):
        if field in changes and (changes[field] is not None or field == "job_title"):
            setattr(user, field, changes[field].strip() if isinstance(changes[field], str) else changes[field])
    if "department_id" in changes:
        _validate_department(db, changes["department_id"])
        user.department_id = changes["department_id"]
    if "manager_id" in changes:
        _validate_manager(db, user.id, changes["manager_id"])
        user.manager_id = changes["manager_id"]

    if "role" in changes and changes["role"] is not None and changes["role"] != user.role:
        new_role = Role(changes["role"])
        if user.id == actor.id:
            raise ValidationFailed("You cannot change your own role")
        if user.role == Role.ADMIN and user.is_active and _active_admin_count(db) <= 1:
            raise ValidationFailed("Cannot demote the last active administrator")
        if new_role == Role.EMPLOYEE and _direct_report_count(db, user.id) > 0:
            raise ValidationFailed("Reassign this person's direct reports before changing their role to EMPLOYEE")
        user.role = new_role

    if "is_active" in changes and changes["is_active"] is not None and changes["is_active"] != user.is_active:
        if not changes["is_active"]:
            if user.id == actor.id:
                raise ValidationFailed("You cannot deactivate your own account")
            if user.role == Role.ADMIN and _active_admin_count(db) <= 1:
                raise ValidationFailed("Cannot deactivate the last active administrator")
            if _direct_report_count(db, user.id) > 0:
                raise ValidationFailed("Reassign this person's direct reports before deactivating them")
            revoke_all_sessions(db, user.id)
        user.is_active = changes["is_active"]

    db.flush()
    after = user_snapshot(user)
    b, a = audit.changes(before, after)
    if a:
        action = "USER_UPDATED"
        if "role" in a:
            action = "USER_ROLE_CHANGED"
        elif "manager_id" in a and len(a) == 1:
            action = "USER_MANAGER_ASSIGNED"
        elif "is_active" in a and len(a) == 1:
            action = "USER_ACTIVATED" if a["is_active"] else "USER_DEACTIVATED"
        audit.record(db, actor_id=actor.id, action=action, entity_type="user", entity_id=user.id,
                     before=b, after=a, ctx=ctx)
    _commit_unique(db)
    db.refresh(user)  # reload manager/department relationships after FK changes
    return user


def reset_password(db: Session, actor: User, user_id: int, password: str, ctx: RequestContext) -> None:
    user = db.get(User, user_id)
    if user is None:
        raise NotFound("User not found")
    validate_password_strength(password)
    user.password_hash = hash_password(password)
    revoke_all_sessions(db, user.id)
    audit.record(db, actor_id=actor.id, action="USER_PASSWORD_RESET", entity_type="user", entity_id=user.id,
                 ctx=ctx)
    db.commit()


# ---------------------------------------------------------------- departments

def dept_snapshot(d: Department) -> dict:
    return {"name": d.name, "description": d.description, "department_head_id": d.department_head_id}


def _validate_dept(db: Session, name: str, head_id: int | None, dept_id: int | None) -> str:
    name = name.strip()
    if not name:
        raise ValidationFailed("Department name is required")
    clash = db.scalar(select(Department.id).where(func.lower(Department.name) == name.lower()))
    if clash is not None and clash != dept_id:
        raise Conflict("A department with this name already exists")
    if head_id is not None:
        head = db.get(User, head_id)
        if head is None or not head.is_active:
            raise ValidationFailed("Department head must be an active user")
    return name


def create_department(db: Session, actor: User, data: dict, ctx: RequestContext) -> Department:
    name = _validate_dept(db, data["name"], data.get("department_head_id"), None)
    dept = Department(name=name, description=data.get("description"),
                      department_head_id=data.get("department_head_id"))
    db.add(dept)
    db.flush()
    audit.record(db, actor_id=actor.id, action="DEPARTMENT_CREATED", entity_type="department",
                 entity_id=dept.id, after=dept_snapshot(dept), ctx=ctx)
    _commit_unique(db)
    db.refresh(dept)
    return dept


def update_department(db: Session, actor: User, dept_id: int, changes: dict, ctx: RequestContext) -> Department:
    dept = db.get(Department, dept_id)
    if dept is None:
        raise NotFound("Department not found")
    before = dept_snapshot(dept)
    name = _validate_dept(db, changes.get("name") or dept.name,
                          changes.get("department_head_id", dept.department_head_id), dept.id)
    dept.name = name
    if "description" in changes:
        dept.description = changes["description"]
    if "department_head_id" in changes:
        dept.department_head_id = changes["department_head_id"]
    db.flush()
    b, a = audit.changes(before, dept_snapshot(dept))
    if a:
        audit.record(db, actor_id=actor.id, action="DEPARTMENT_UPDATED", entity_type="department",
                     entity_id=dept.id, before=b, after=a, ctx=ctx)
    _commit_unique(db)
    db.refresh(dept)
    return dept


def delete_department(db: Session, actor: User, dept_id: int, ctx: RequestContext) -> None:
    dept = db.get(Department, dept_id)
    if dept is None:
        raise NotFound("Department not found")
    members = db.scalar(select(func.count()).select_from(User).where(User.department_id == dept_id))
    if members:
        raise Conflict("Move all employees out of this department before deleting it")
    audit.record(db, actor_id=actor.id, action="DEPARTMENT_DELETED", entity_type="department", entity_id=dept.id,
                 before=dept_snapshot(dept), ctx=ctx)
    db.delete(dept)
    db.commit()
