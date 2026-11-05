"""Shared FastAPI dependencies: DB session, request context, authentication, CSRF, roles."""

from dataclasses import dataclass
from datetime import date
from typing import Annotated

from fastapi import Depends, Query, Request
from sqlalchemy.orm import Session

from app.core.config import get_settings
from app.core.context import RequestContext
from app.core.errors import Forbidden
from app.core.security import tokens_equal
from app.core.timeutil import today_local
from app.db.session import get_db
from app.models import Role, User, UserSession
from app.schemas.common import DateRange, PageParams
from app.services import auth_service

SAFE_METHODS = {"GET", "HEAD", "OPTIONS"}

DB = Annotated[Session, Depends(get_db)]


def client_ip(request: Request) -> str | None:
    if get_settings().trust_proxy_headers:
        forwarded = request.headers.get("x-forwarded-for")
        if forwarded:
            return forwarded.split(",")[0].strip()[:64]
    return request.client.host if request.client else None


def get_context(request: Request) -> RequestContext:
    return RequestContext(
        request_id=getattr(request.state, "request_id", None),
        ip_address=client_ip(request),
        user_agent=request.headers.get("user-agent"),
    )


Ctx = Annotated[RequestContext, Depends(get_context)]


@dataclass
class Auth:
    user: User
    session: UserSession


def get_auth(request: Request, db: DB) -> Auth:
    s = get_settings()
    user, session = auth_service.authenticate(db, request.cookies.get(s.session_cookie_name))
    if request.method not in SAFE_METHODS:
        # Double-submit check: the header must echo this session's CSRF token.
        if not tokens_equal(request.headers.get(s.csrf_header_name), session.csrf_token):
            raise Forbidden("Missing or invalid CSRF token", code="csrf_failed")
    return Auth(user=user, session=session)


AuthDep = Annotated[Auth, Depends(get_auth)]


def get_current_user(auth: AuthDep) -> User:
    return auth.user


CurrentUser = Annotated[User, Depends(get_current_user)]


def require_roles(*roles: Role):
    def checker(user: CurrentUser) -> User:
        if user.role not in roles:
            raise Forbidden("You do not have permission to perform this action")
        return user

    return checker


ManagerOrAdmin = Annotated[User, Depends(require_roles(Role.MANAGER, Role.ADMIN))]
AdminUser = Annotated[User, Depends(require_roles(Role.ADMIN))]


def date_range(
    start: date | None = Query(None, description="Start date (office-local, inclusive)"),
    end: date | None = Query(None, description="End date (office-local, inclusive)"),
) -> DateRange:
    today = today_local()
    if end is None:
        end = today
    if start is None:
        start = end.replace(day=1)
    return DateRange(start=start, end=end)


Range = Annotated[DateRange, Depends(date_range)]


def page_params(page: int = Query(1, ge=1), page_size: int = Query(20, ge=1, le=200)) -> PageParams:
    return PageParams(page=page, page_size=page_size)


Paging = Annotated[PageParams, Depends(page_params)]
