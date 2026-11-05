"""Administrative endpoints: users, departments, schedules, holidays, policy, audit log."""

from datetime import date

from fastapi import APIRouter, Query
from sqlalchemy import func, or_, select

from app.api.deps import DB, AdminUser, CurrentUser, Ctx, Paging
from app.core.errors import NotFound
from app.core.timeutil import local_day_bounds
from app.models import AuditLog, Department, Holiday, Role, User, WorkSchedule
from app.schemas.admin import (
    AuditOut,
    DepartmentCreate,
    DepartmentOut,
    DepartmentUpdate,
    HolidayCreate,
    HolidayOut,
    PasswordReset,
    PolicyOut,
    PolicyUpdate,
    ScheduleBase,
    ScheduleOut,
    ScheduleUpdate,
    UserAdminOut,
    UserCreate,
    UserUpdate,
)
from app.schemas.common import MessageResponse, Page
from app.services import admin_users, configuration
from app.services.policy import get_policy

router = APIRouter(tags=["administration"])


# ------------------------------------------------------------------------ users

@router.get("/admin/users", response_model=Page[UserAdminOut])
def list_users(_: AdminUser, db: DB, paging: Paging, search: str | None = Query(None, max_length=100),
               role: Role | None = None, department_id: int | None = None, manager_id: int | None = None,
               is_active: bool | None = None):
    stmt = select(User)
    if search:
        like = f"%{search.strip().lower()}%"
        stmt = stmt.where(or_(func.lower(User.full_name).like(like), func.lower(User.email).like(like),
                              func.lower(User.employee_code).like(like)))
    if role:
        stmt = stmt.where(User.role == role)
    if department_id is not None:
        stmt = stmt.where(User.department_id == department_id)
    if manager_id is not None:
        stmt = stmt.where(User.manager_id == manager_id)
    if is_active is not None:
        stmt = stmt.where(User.is_active.is_(is_active))
    total = db.scalar(select(func.count()).select_from(stmt.subquery()))
    rows = db.scalars(stmt.order_by(User.full_name).offset(paging.offset).limit(paging.page_size)).unique()
    return Page(items=[UserAdminOut.model_validate(u) for u in rows], total=total, page=paging.page,
                page_size=paging.page_size)


@router.get("/admin/users/{user_id}", response_model=UserAdminOut)
def get_user(user_id: int, _: AdminUser, db: DB):
    user = db.get(User, user_id)
    if user is None:
        raise NotFound("User not found")
    return user


@router.post("/admin/users", response_model=UserAdminOut, status_code=201)
def create_user(body: UserCreate, admin: AdminUser, db: DB, ctx: Ctx):
    return admin_users.create_user(db, admin, body.model_dump(), ctx)


@router.patch("/admin/users/{user_id}", response_model=UserAdminOut)
def update_user(user_id: int, body: UserUpdate, admin: AdminUser, db: DB, ctx: Ctx):
    return admin_users.update_user(db, admin, user_id, body.model_dump(exclude_unset=True), ctx)


@router.post("/admin/users/{user_id}/reset-password", response_model=MessageResponse)
def reset_password(user_id: int, body: PasswordReset, admin: AdminUser, db: DB, ctx: Ctx):
    admin_users.reset_password(db, admin, user_id, body.password, ctx)
    return MessageResponse(message="Password reset; the user's sessions were signed out")


@router.get("/managers", response_model=list[UserAdminOut], summary="Active users who can be assigned as managers")
def list_managers(_: AdminUser, db: DB):
    stmt = select(User).where(User.role.in_([Role.MANAGER, Role.ADMIN]), User.is_active.is_(True))
    return list(db.scalars(stmt.order_by(User.full_name)).unique())


# ------------------------------------------------------------------ departments

def _dept_out(db, d: Department) -> DepartmentOut:
    out = DepartmentOut.model_validate(d)
    out.member_count = db.scalar(select(func.count()).select_from(User).where(User.department_id == d.id,
                                                                             User.is_active.is_(True)))
    return out


@router.get("/departments", response_model=list[DepartmentOut])
def list_departments(_: CurrentUser, db: DB):
    return [_dept_out(db, d) for d in db.scalars(select(Department).order_by(Department.name))]


@router.post("/admin/departments", response_model=DepartmentOut, status_code=201)
def create_department(body: DepartmentCreate, admin: AdminUser, db: DB, ctx: Ctx):
    return _dept_out(db, admin_users.create_department(db, admin, body.model_dump(), ctx))


@router.patch("/admin/departments/{dept_id}", response_model=DepartmentOut)
def update_department(dept_id: int, body: DepartmentUpdate, admin: AdminUser, db: DB, ctx: Ctx):
    return _dept_out(db, admin_users.update_department(db, admin, dept_id, body.model_dump(exclude_unset=True), ctx))


@router.delete("/admin/departments/{dept_id}", response_model=MessageResponse)
def delete_department(dept_id: int, admin: AdminUser, db: DB, ctx: Ctx):
    admin_users.delete_department(db, admin, dept_id, ctx)
    return MessageResponse(message="Department deleted")


# -------------------------------------------------------------------- schedules

def _schedule_out(db, s: WorkSchedule) -> ScheduleOut:
    out = ScheduleOut.model_validate(s)
    if s.employee_id:
        u = db.get(User, s.employee_id)
        out.target_label = f"Employee: {u.full_name}" if u else "Employee"
    elif s.department_id:
        d = db.get(Department, s.department_id)
        out.target_label = f"Department: {d.name}" if d else "Department"
    else:
        out.target_label = "Organisation default"
    return out


@router.get("/admin/schedules", response_model=list[ScheduleOut])
def list_schedules(_: AdminUser, db: DB):
    rows = db.scalars(select(WorkSchedule).order_by(WorkSchedule.employee_id.is_not(None),
                                                    WorkSchedule.department_id.is_not(None),
                                                    WorkSchedule.effective_from.desc()))
    return [_schedule_out(db, s) for s in rows]


@router.post("/admin/schedules", response_model=ScheduleOut, status_code=201)
def create_schedule(body: ScheduleBase, admin: AdminUser, db: DB, ctx: Ctx):
    return _schedule_out(db, configuration.create_schedule(db, admin, body.model_dump(), ctx))


@router.patch("/admin/schedules/{schedule_id}", response_model=ScheduleOut)
def update_schedule(schedule_id: int, body: ScheduleUpdate, admin: AdminUser, db: DB, ctx: Ctx):
    return _schedule_out(db, configuration.update_schedule(db, admin, schedule_id,
                                                           body.model_dump(exclude_unset=True), ctx))


@router.delete("/admin/schedules/{schedule_id}", response_model=MessageResponse)
def delete_schedule(schedule_id: int, admin: AdminUser, db: DB, ctx: Ctx):
    configuration.delete_schedule(db, admin, schedule_id, ctx)
    return MessageResponse(message="Schedule deleted")


# --------------------------------------------------------------------- holidays

@router.get("/holidays", response_model=list[HolidayOut], summary="Holidays (relevant to me unless admin)")
def list_holidays(user: CurrentUser, db: DB, start: date | None = None, end: date | None = None):
    stmt = select(Holiday)
    if start:
        stmt = stmt.where(Holiday.holiday_date >= start)
    if end:
        stmt = stmt.where(Holiday.holiday_date <= end)
    if user.role != Role.ADMIN:
        stmt = stmt.where(or_(Holiday.department_id.is_(None), Holiday.department_id == user.department_id))
    return list(db.scalars(stmt.order_by(Holiday.holiday_date)))


@router.post("/admin/holidays", response_model=HolidayOut, status_code=201)
def create_holiday(body: HolidayCreate, admin: AdminUser, db: DB, ctx: Ctx):
    return configuration.create_holiday(db, admin, body.model_dump(), ctx)


@router.delete("/admin/holidays/{holiday_id}", response_model=MessageResponse)
def delete_holiday(holiday_id: int, admin: AdminUser, db: DB, ctx: Ctx):
    configuration.delete_holiday(db, admin, holiday_id, ctx)
    return MessageResponse(message="Holiday deleted")


# ----------------------------------------------------------------------- policy

@router.get("/policy", response_model=PolicyOut)
def read_policy(_: CurrentUser, db: DB):
    policy = get_policy(db)
    db.commit()
    return policy


@router.put("/admin/policy", response_model=PolicyOut)
def update_policy(body: PolicyUpdate, admin: AdminUser, db: DB, ctx: Ctx):
    return configuration.update_policy(db, admin, body.model_dump(exclude_unset=True), ctx)


# -------------------------------------------------------------------- audit log

@router.get("/admin/audit-logs", response_model=Page[AuditOut])
def audit_logs(_: AdminUser, db: DB, paging: Paging, action: str | None = Query(None, max_length=64),
               entity_type: str | None = Query(None, max_length=64), actor_id: int | None = None,
               search: str | None = Query(None, max_length=100), start: date | None = None,
               end: date | None = None):
    stmt = select(AuditLog, User.full_name).outerjoin(User, User.id == AuditLog.actor_user_id)
    if action:
        stmt = stmt.where(AuditLog.action == action)
    if entity_type:
        stmt = stmt.where(AuditLog.entity_type == entity_type)
    if actor_id is not None:
        stmt = stmt.where(AuditLog.actor_user_id == actor_id)
    if search:
        like = f"%{search.strip().lower()}%"
        stmt = stmt.where(or_(func.lower(AuditLog.action).like(like), func.lower(User.full_name).like(like),
                              AuditLog.entity_id == search.strip()))
    if start:
        stmt = stmt.where(AuditLog.created_at >= local_day_bounds(start)[0])
    if end:
        stmt = stmt.where(AuditLog.created_at < local_day_bounds(end)[1])
    total = db.scalar(select(func.count()).select_from(stmt.subquery()))
    rows = db.execute(stmt.order_by(AuditLog.created_at.desc(), AuditLog.id.desc())
                      .offset(paging.offset).limit(paging.page_size)).all()
    items = []
    for log, actor_name in rows:
        out = AuditOut.model_validate(log)
        out.actor_name = actor_name
        items.append(out)
    return Page(items=items, total=total, page=paging.page, page_size=paging.page_size)
