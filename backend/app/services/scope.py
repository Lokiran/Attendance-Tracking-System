"""Reporting-scope authorisation.

Rule: a MANAGER can see their entire reporting subtree (direct and indirect reports), never
peers or unrelated teams. Being a department head grants no extra access. ADMIN sees everyone.
EMPLOYEE sees only themselves. Every protected query goes through these helpers.
"""

from sqlalchemy import func, select
from sqlalchemy.orm import Session, aliased, selectinload

from app.core.errors import Forbidden, NotFound
from app.models import Role, User


def subtree_ids(db: Session, root_id: int) -> set[int]:
    base = select(User.id).where(User.manager_id == root_id).cte("reporting_tree", recursive=True)
    child = aliased(User)
    tree = base.union(select(child.id).where(child.manager_id == base.c.id))  # UNION also stops cycles
    return set(db.scalars(select(tree.c.id)))


def manager_chain_ids(db: Session, user_id: int) -> list[int]:
    """Ids of user's manager, manager's manager, ... (bounded walk; stops on any cycle)."""
    chain: list[int] = []
    current = db.get(User, user_id)
    seen = {user_id}
    while current is not None and current.manager_id is not None and current.manager_id not in seen:
        chain.append(current.manager_id)
        seen.add(current.manager_id)
        current = db.get(User, current.manager_id)
    return chain


def can_view_employee(db: Session, viewer: User, employee_id: int) -> bool:
    if viewer.id == employee_id or viewer.role == Role.ADMIN:
        return True
    if viewer.role == Role.MANAGER:
        return employee_id in subtree_ids(db, viewer.id)
    return False


def get_viewable_employee(db: Session, viewer: User, employee_id: int) -> User:
    employee = db.get(User, employee_id)
    # Same response for "missing" and "not yours" so ids cannot be probed.
    if employee is None or not can_view_employee(db, viewer, employee_id):
        raise NotFound("Employee not found")
    return employee


def resolve_scope(
    db: Session,
    viewer: User,
    *,
    manager_id: int | None = None,
    department_id: int | None = None,
    include_inactive: bool = False,
    tracked_only: bool = True,
    search: str | None = None,
    limit: int | None = None,
) -> list[User]:
    """Employees the viewer may report on, optionally narrowed to a manager's subtree or department,
    a name / employee-ID search and a maximum number of results (for search-as-you-type pickers)."""
    if viewer.role == Role.EMPLOYEE:
        raise Forbidden("Team data is only available to managers and administrators")

    # Department and manager come from two small IN queries instead of being joined onto every row:
    # noticeably faster when the scope is thousands of people.
    stmt = select(User).options(selectinload(User.department), selectinload(User.manager))
    if viewer.role == Role.MANAGER:
        allowed = subtree_ids(db, viewer.id)
        if manager_id is not None and manager_id != viewer.id:
            if manager_id not in allowed:
                raise NotFound("Manager not found")
            allowed = subtree_ids(db, manager_id)
        stmt = stmt.where(User.id.in_(allowed)) if allowed else stmt.where(False)
    elif manager_id is not None:
        if db.get(User, manager_id) is None:
            raise NotFound("Manager not found")
        ids = subtree_ids(db, manager_id)
        stmt = stmt.where(User.id.in_(ids)) if ids else stmt.where(False)

    if department_id is not None:
        stmt = stmt.where(User.department_id == department_id)
    if not include_inactive:
        stmt = stmt.where(User.is_active.is_(True))
    if tracked_only:
        stmt = stmt.where(User.attendance_tracked.is_(True))
    if search and search.strip():
        pattern = f"%{search.strip().lower()}%"
        stmt = stmt.where(func.lower(User.full_name).like(pattern) | func.lower(User.employee_code).like(pattern))
    stmt = stmt.order_by(User.full_name, User.id)
    if limit is not None:
        stmt = stmt.limit(limit)
    return list(db.scalars(stmt).unique())
