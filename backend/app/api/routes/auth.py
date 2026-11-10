import secrets

from fastapi import APIRouter, Query, Request, Response
from fastapi.responses import RedirectResponse
from sqlalchemy import select

from app.api.deps import DB, AuthDep, Ctx
from app.core.config import get_settings
from app.core.errors import Forbidden, NotFound, Unauthorized
from app.core.timeutil import office_tz
from app.models import User
from app.schemas.auth import ChangePasswordRequest, DevLoginRequest, LoginRequest, MeResponse, Profile, ProvidersOut
from app.schemas.common import MessageResponse
from app.services import auth_service, entra

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


@router.get("/providers", response_model=ProvidersOut, summary="Sign-in methods the login page should offer")
def providers():
    s = get_settings()
    return ProvidersOut(password=s.password_login_enabled, entra=s.entra_enabled,
                        dev=s.dev_login_enabled and s.app_env != "production")


@router.post("/login", response_model=MeResponse, summary="Sign in with email and password")
def login(body: LoginRequest, response: Response, db: DB, ctx: Ctx):
    if not get_settings().password_login_enabled:
        raise Forbidden("Password sign-in is turned off. Sign in with Microsoft.", code="password_login_disabled")
    result = auth_service.login(db, body.email, body.password, ctx)
    _set_cookies(response, result.token, result.session.csrf_token)
    return _me(db, result.user, result.session.csrf_token)


@router.post("/dev-login", response_model=MeResponse, include_in_schema=False)
def dev_login(body: DevLoginRequest, response: Response, db: DB, ctx: Ctx):
    """Local testing only: sign in as an active user without a password. Off unless DEV_LOGIN_ENABLED,
    and impossible in production (settings refuse to start). Every use is audited as method "dev"."""
    s = get_settings()
    if not s.dev_login_enabled or s.app_env == "production":
        raise NotFound("Not found")
    user = db.scalar(select(User).where(User.email == body.email.strip().lower()))
    if user is None or not user.is_active:
        raise Unauthorized("No active account with that email", code="invalid_credentials")
    result = auth_service.start_session(db, user, ctx, method="dev")
    _set_cookies(response, result.token, result.session.csrf_token)
    return _me(db, result.user, result.session.csrf_token)


@router.get("/entra/login", summary="Start Microsoft Entra ID sign-in (redirects to Microsoft)")
def entra_login(next: str | None = Query(None, max_length=500)):
    s = get_settings()
    if not s.entra_enabled:
        raise NotFound("Microsoft sign-in is not configured")
    state, nonce = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    verifier, challenge = entra.new_pkce()
    redirect = RedirectResponse(entra.authorize_url(state, nonce, challenge), status_code=302)
    redirect.set_cookie(entra.FLOW_COOKIE, entra.dump_flow({"state": state, "nonce": nonce, "verifier": verifier,
                                                            "next": entra.safe_next(next)}),
                        max_age=entra.FLOW_TTL_SECONDS, path=entra.FLOW_COOKIE_PATH, httponly=True,
                        secure=s.secure_cookies, samesite="lax")  # lax: sent on Microsoft's top-level redirect back
    return redirect


@router.get("/entra/callback", include_in_schema=False)
def entra_callback(request: Request, db: DB, ctx: Ctx, code: str | None = None, state: str | None = None,
                   error: str | None = None):
    s = get_settings()
    if not s.entra_enabled:
        raise NotFound("Microsoft sign-in is not configured")
    flow = entra.load_flow(request.cookies.get(entra.FLOW_COOKIE))
    try:
        if error:
            raise entra.EntraSignInError("entra_denied" if error == "access_denied" else "entra_error", error[:64])
        if flow is None:
            raise entra.EntraSignInError("sign_in_expired", "flow cookie missing, invalid or expired")
        if not state or not secrets.compare_digest(state, flow["state"]) or not code:
            raise entra.EntraSignInError("sign_in_expired", "state mismatch or no code")
        claims = entra.validate_id_token(entra.exchange_code(code, flow["verifier"]), flow["nonce"])
        user = entra.find_user(db, claims, ctx)
    except entra.EntraSignInError as exc:
        db.rollback()
        entra.logger.warning("Entra sign-in refused: %s (%s)", exc.code, exc.log_detail)
        response = RedirectResponse(f"/login?error={exc.code}", status_code=302)
    else:
        result = auth_service.start_session(db, user, ctx, method="entra")
        response = RedirectResponse(flow["next"], status_code=302)
        _set_cookies(response, result.token, result.session.csrf_token)
    response.delete_cookie(entra.FLOW_COOKIE, path=entra.FLOW_COOKIE_PATH, secure=s.secure_cookies, samesite="lax")
    return response


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
