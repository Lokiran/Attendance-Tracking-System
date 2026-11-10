"""Login, server-side sessions and brute-force throttling."""

import logging
from dataclasses import dataclass
from datetime import timedelta

from sqlalchemy import func, select, update
from sqlalchemy.orm import Session

from app.core import clock
from app.core.config import get_settings
from app.core.context import RequestContext
from app.core.errors import TooManyRequests, Unauthorized
from app.core.security import (
    generate_token,
    hash_password,
    hash_token,
    needs_rehash,
    validate_password_strength,
    verify_password,
)
from app.models import LoginAttempt, User, UserSession
from app.services import audit

logger = logging.getLogger("ats.auth")

LAST_SEEN_RESOLUTION = timedelta(seconds=60)


@dataclass
class LoginResult:
    user: User
    token: str
    session: UserSession


def _check_throttle(db: Session, email: str, ip: str | None) -> None:
    s = get_settings()
    since = clock.utcnow() - timedelta(minutes=s.login_window_minutes)
    by_email = db.scalar(
        select(func.count()).select_from(LoginAttempt).where(
            LoginAttempt.email == email, LoginAttempt.succeeded.is_(False), LoginAttempt.created_at >= since
        )
    )
    by_ip = 0
    if ip:
        by_ip = db.scalar(
            select(func.count()).select_from(LoginAttempt).where(
                LoginAttempt.ip_address == ip, LoginAttempt.succeeded.is_(False), LoginAttempt.created_at >= since
            )
        )
    if by_email >= s.login_max_failures or by_ip >= s.login_ip_max_failures:
        raise TooManyRequests(
            f"Too many failed sign-in attempts. Try again in {s.login_window_minutes} minutes.",
            retry_after_seconds=s.login_window_minutes * 60,
        )


def login(db: Session, email: str, password: str, ctx: RequestContext) -> LoginResult:
    email = email.strip().lower()
    _check_throttle(db, email, ctx.ip_address)
    user = db.scalar(select(User).where(User.email == email))
    ok = verify_password(user.password_hash if user else None, password)
    if not ok or user is None or not user.is_active:
        db.add(LoginAttempt(email=email, ip_address=ctx.ip_address, succeeded=False))
        db.commit()
        logger.info("failed login for email hash=%s", hash_token(email)[:12])
        raise Unauthorized("Invalid email or password", code="invalid_credentials")

    if needs_rehash(user.password_hash):
        user.password_hash = hash_password(password)
    db.add(LoginAttempt(email=email, ip_address=ctx.ip_address, succeeded=True))
    return start_session(db, user, ctx, method="password")


def start_session(db: Session, user: User, ctx: RequestContext, *, method: str) -> LoginResult:
    """Create a server-side session for an already authenticated, active user (any sign-in method)."""
    s = get_settings()
    now = clock.utcnow()
    token = generate_token()
    session = UserSession(
        user_id=user.id, token_hash=hash_token(token), csrf_token=generate_token(), created_at=now,
        last_seen_at=now, expires_at=now + timedelta(hours=s.session_absolute_hours),
        ip_address=ctx.ip_address, user_agent=(ctx.user_agent or "")[:255] or None,
    )
    db.add(session)
    audit.record(db, actor_id=user.id, action="LOGIN", entity_type="user", entity_id=user.id,
                 after={"method": method}, ctx=ctx)
    db.commit()
    return LoginResult(user=user, token=token, session=session)


def authenticate(db: Session, token: str | None) -> tuple[User, UserSession]:
    if not token:
        raise Unauthorized("Not signed in", code="not_authenticated")
    session = db.scalar(select(UserSession).where(UserSession.token_hash == hash_token(token)))
    now = clock.utcnow()
    s = get_settings()
    if session is None or session.revoked_at is not None:
        raise Unauthorized("Your session is no longer valid. Please sign in again.", code="session_invalid")
    if session.expires_at <= now or session.last_seen_at + timedelta(minutes=s.session_idle_minutes) <= now:
        raise Unauthorized("Your session has expired. Please sign in again.", code="session_expired")
    user = db.get(User, session.user_id)
    if user is None or not user.is_active:
        raise Unauthorized("Your account is inactive", code="account_inactive")
    if now - session.last_seen_at >= LAST_SEEN_RESOLUTION:
        session.last_seen_at = now
        db.commit()
    return user, session


def logout(db: Session, session: UserSession, ctx: RequestContext) -> None:
    session.revoked_at = clock.utcnow()
    audit.record(db, actor_id=session.user_id, action="LOGOUT", entity_type="user", entity_id=session.user_id,
                 ctx=ctx)
    db.commit()


def revoke_all_sessions(db: Session, user_id: int, except_session_id: int | None = None) -> None:
    stmt = update(UserSession).where(UserSession.user_id == user_id, UserSession.revoked_at.is_(None))
    if except_session_id is not None:
        stmt = stmt.where(UserSession.id != except_session_id)
    db.execute(stmt.values(revoked_at=clock.utcnow()))


def change_password(db: Session, user: User, session: UserSession, current: str, new: str,
                    ctx: RequestContext) -> None:
    if not verify_password(user.password_hash, current):
        raise Unauthorized("Current password is incorrect", code="invalid_credentials")
    validate_password_strength(new)
    user.password_hash = hash_password(new)
    revoke_all_sessions(db, user.id, except_session_id=session.id)
    audit.record(db, actor_id=user.id, action="PASSWORD_CHANGED", entity_type="user", entity_id=user.id, ctx=ctx)
    db.commit()
