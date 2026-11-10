"""Microsoft Entra ID sign-in (OpenID Connect, authorization code flow with PKCE).

Flow:
1. ``/api/auth/entra/login`` creates ``state`` (CSRF for the round trip), ``nonce`` (binds the ID token
   to this attempt) and a PKCE verifier. It keeps them in a short-lived HMAC-signed HttpOnly cookie and
   redirects to Microsoft.
2. Microsoft redirects back to ``/api/auth/entra/callback`` with ``code`` and ``state``. The app checks
   ``state`` against the cookie, exchanges the code (with the PKCE verifier and the client secret) and
   validates the ID token: RS256 signature with the tenant's published keys, issuer, audience, expiry,
   nonce and tenant id.
3. The person is matched to an existing, active account by Entra object id (``users.graph_user_id``) or,
   the first time, by email; the object id is then linked. Nobody is created automatically: HR adds
   people, Entra only proves who they are. Roles stay managed in the app.

Network calls are isolated in ``_post_token`` and ``_signing_key`` so tests can replace them.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import logging
import secrets
import time
import urllib.error
import urllib.parse
import urllib.request
from functools import lru_cache

import jwt
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core.config import get_settings
from app.core.context import RequestContext
from app.models import User
from app.services import audit

logger = logging.getLogger("ats.entra")

FLOW_COOKIE = "ats_oidc"
FLOW_COOKIE_PATH = "/api/auth/entra"
FLOW_TTL_SECONDS = 600
SCOPES = "openid profile email"


class EntraSignInError(Exception):
    """A sign-in attempt that must be refused. ``code`` is shown to the login page (no details leaked)."""

    def __init__(self, code: str, log_detail: str = ""):
        super().__init__(code)
        self.code = code
        self.log_detail = log_detail


# ------------------------------------------------------------------ endpoints and URLs

def _tenant_base() -> str:
    s = get_settings()
    return f"{s.entra_authority.rstrip('/')}/{s.entra_tenant_id}"


def issuer() -> str:
    return f"{_tenant_base()}/v2.0"


def authorize_url(state: str, nonce: str, code_challenge: str) -> str:
    s = get_settings()
    query = urllib.parse.urlencode({
        "client_id": s.entra_client_id, "response_type": "code", "redirect_uri": s.entra_redirect_uri,
        "response_mode": "query", "scope": SCOPES, "state": state, "nonce": nonce,
        "code_challenge": code_challenge, "code_challenge_method": "S256",
    })
    return f"{_tenant_base()}/oauth2/v2.0/authorize?{query}"


def new_pkce() -> tuple[str, str]:
    verifier = secrets.token_urlsafe(64)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    return verifier, challenge


# ------------------------------------------------------------------ signed flow cookie

def _b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def _unb64(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def _mac(payload: str) -> str:
    key = hashlib.sha256(("entra-flow:" + get_settings().secret_key).encode()).digest()
    return _b64(hmac.new(key, payload.encode(), hashlib.sha256).digest())


def dump_flow(data: dict) -> str:
    payload = _b64(json.dumps({**data, "exp": int(time.time()) + FLOW_TTL_SECONDS}).encode())
    return f"{payload}.{_mac(payload)}"


def load_flow(token: str | None) -> dict | None:
    """The flow data, or None if the cookie is missing, tampered with or expired."""
    if not token or "." not in token:
        return None
    payload, mac = token.rsplit(".", 1)
    if not hmac.compare_digest(mac, _mac(payload)):
        return None
    try:
        data = json.loads(_unb64(payload))
    except (ValueError, json.JSONDecodeError):
        return None
    return data if isinstance(data, dict) and data.get("exp", 0) >= time.time() else None


def safe_next(path: str | None) -> str:
    """Only same-site relative paths, so the sign-in cannot be used as an open redirect."""
    if not path or not path.startswith("/") or path.startswith("//") or "\\" in path or "://" in path:
        return "/"
    return path


# ------------------------------------------------------------------ token exchange and validation

def _post_token(form: dict) -> dict:
    request = urllib.request.Request(
        f"{_tenant_base()}/oauth2/v2.0/token", data=urllib.parse.urlencode(form).encode(),
        headers={"Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=10) as response:  # noqa: S310 (https URL from settings)
            return json.load(response)
    except urllib.error.HTTPError as exc:
        try:
            body = json.load(exc)
        except (ValueError, json.JSONDecodeError):
            body = {}
        raise EntraSignInError("token_exchange_failed", f"HTTP {exc.code} {body.get('error', '')}") from exc
    except (urllib.error.URLError, TimeoutError) as exc:
        raise EntraSignInError("entra_unreachable", type(exc).__name__) from exc


@lru_cache(maxsize=4)
def _jwks_client(jwks_uri: str) -> jwt.PyJWKClient:
    return jwt.PyJWKClient(jwks_uri, cache_keys=True, lifespan=3600)


def _signing_key(id_token: str):
    try:
        return _jwks_client(f"{_tenant_base()}/discovery/v2.0/keys").get_signing_key_from_jwt(id_token).key
    except jwt.PyJWKClientError as exc:
        raise EntraSignInError("invalid_token", f"signing key: {exc}") from exc


def exchange_code(code: str, verifier: str) -> str:
    """Returns the ID token for an authorization code."""
    s = get_settings()
    tokens = _post_token({
        "client_id": s.entra_client_id, "client_secret": s.entra_client_secret, "grant_type": "authorization_code",
        "code": code, "redirect_uri": s.entra_redirect_uri, "code_verifier": verifier, "scope": SCOPES,
    })
    id_token = tokens.get("id_token")
    if not id_token:
        raise EntraSignInError("token_exchange_failed", "no id_token in response")
    return id_token


def validate_id_token(id_token: str, nonce: str) -> dict:
    s = get_settings()
    try:
        claims = jwt.decode(
            id_token, _signing_key(id_token), algorithms=["RS256"], audience=s.entra_client_id, issuer=issuer(),
            leeway=60, options={"require": ["exp", "iat", "aud", "iss", "sub"]},
        )
    except jwt.InvalidTokenError as exc:
        raise EntraSignInError("invalid_token", type(exc).__name__) from exc
    if not hmac.compare_digest(str(claims.get("nonce", "")), nonce):
        raise EntraSignInError("invalid_token", "nonce mismatch")
    if claims.get("tid") != s.entra_tenant_id:
        raise EntraSignInError("invalid_token", "tenant mismatch")
    if not claims.get("oid"):
        raise EntraSignInError("invalid_token", "no oid claim")
    return claims


# ------------------------------------------------------------------ account matching

def find_user(db: Session, claims: dict, ctx: RequestContext) -> User:
    """The active account for these claims; links the Entra object id on first sign-in by email."""
    oid = str(claims["oid"]).lower()
    user = db.scalar(select(User).where(func.lower(User.graph_user_id) == oid))
    if user is None:
        email = str(claims.get("email") or claims.get("preferred_username") or "").strip().lower()
        if email:
            user = db.scalar(select(User).where(User.email == email))
        if user is None:
            raise EntraSignInError("no_account", "no ATS account for this Microsoft account")
        if user.graph_user_id and user.graph_user_id.lower() != oid:
            # The account is already linked to a different Microsoft identity: never re-link silently.
            raise EntraSignInError("account_mismatch", f"user {user.id} linked to another object id")
        user.graph_user_id = oid
        audit.record(db, actor_id=user.id, action="USER_IDENTITY_LINKED", entity_type="user", entity_id=user.id,
                     after={"graph_user_id": oid, "via": "entra_sign_in"}, ctx=ctx)
    if not user.is_active:
        raise EntraSignInError("account_inactive", f"user {user.id} inactive")
    return user
