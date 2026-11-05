from fastapi import APIRouter, Response
from sqlalchemy import select

from app.api.deps import DB, AuthDep, Ctx
from app.core.config import get_settings
from app.core.timeutil import office_tz
from app.models import User
from app.schemas.auth import ChangePasswordRequest, LoginRequest, MeResponse, Profile
from app.schemas.common import MessageResponse
from app.services import auth_service

router = APIRouter(prefix="/auth", tags=["auth"])


def _set_cookies(response: Response, token: str, csrf: str) -> None:
    s = get_settings()
    common = {"secure": s.secure_cookies, "samesite": s.cookie_samesite, "domain": s.cookie_domain, "path": "/"}
    max_age = s.session_absolute_hours * 3600
    response.set_cookie(s.session_cookie_name, token, httponly=True, max_age=max_age, **common)
    # Readable by the SPA so it can echo the value in the X-CSRF-Token header.
    response.set_cookie(s.csrf_cookie_name, csrf, httponly=False, max_age=max_age, **common)


def _clear_cookies(response: Response) -> None:
    s = get_settings()
    for name in (s.session_cookie_name, s.csrf_cookie_name):
        response.delete_cookie(name, path="/", domain=s.cookie_domain, secure=s.secure_cookies,
                               samesite=s.cookie_samesite)


def _me(db, user: User, csrf: str) -> MeResponse:
    has_reports = db.scalar(select(User.id).where(User.manager_id == user.id).limit(1)) is not None
    return MeResponse(user=Profile.model_validate(user), csrf_token=csrf, office_timezone=office_tz().key,
                      has_reports=has_reports)


@router.post("/login", response_model=MeResponse, summary="Sign in with email and password")
def login(body: LoginRequest, response: Response, db: DB, ctx: Ctx):
    result = auth_service.login(db, body.email, body.password, ctx)
    _set_cookies(response, result.token, result.session.csrf_token)
    return _me(db, result.user, result.session.csrf_token)


@router.post("/logout", response_model=MessageResponse)
def logout(response: Response, auth: AuthDep, db: DB, ctx: Ctx):
    auth_service.logout(db, auth.session, ctx)
    _clear_cookies(response)
    return MessageResponse(message="Signed out")


@router.get("/me", response_model=MeResponse, summary="Current user profile and CSRF token")
def me(auth: AuthDep, db: DB):
    return _me(db, auth.user, auth.session.csrf_token)


@router.post("/change-password", response_model=MessageResponse)
def change_password(body: ChangePasswordRequest, auth: AuthDep, db: DB, ctx: Ctx):
    auth_service.change_password(db, auth.user, auth.session, body.current_password, body.new_password, ctx)
    return MessageResponse(message="Password changed. Other sessions have been signed out.")
