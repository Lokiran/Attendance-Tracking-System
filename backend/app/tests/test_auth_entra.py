"""Microsoft Entra ID sign-in, the dev login bypass and the password-login switch.

No network: Microsoft's token endpoint and signing keys are replaced by a local RSA key, so every
check of the ID token (signature, issuer, audience, expiry, nonce, tenant) runs for real.
"""

import time
from urllib.parse import parse_qs, urlparse

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from pydantic import ValidationError
from sqlalchemy import select

from app.core.config import Settings, get_settings
from app.models import AuditLog
from app.services import entra
from app.tests.conftest import login

TENANT = "11111111-2222-3333-4444-555555555555"
CLIENT = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
OID = "0f0e0d0c-0b0a-0908-0706-050403020100"
KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
OTHER_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)


@pytest.fixture
def entra_on(monkeypatch):
    s = get_settings()
    for name, value in (("entra_tenant_id", TENANT), ("entra_client_id", CLIENT), ("entra_client_secret", "x" * 32),
                        ("entra_redirect_uri", "http://testserver/api/auth/entra/callback")):
        monkeypatch.setattr(s, name, value)
    monkeypatch.setattr(entra, "_signing_key", lambda token: KEY.public_key())
    return s


def id_token(expected_nonce: str, *, key=KEY, **overrides) -> str:
    now = int(time.time())
    claims = {"iss": f"https://login.microsoftonline.com/{TENANT}/v2.0", "aud": CLIENT, "tid": TENANT, "oid": OID,
              "sub": "pairwise-subject", "nonce": expected_nonce, "iat": now, "nbf": now, "exp": now + 3600,
              "preferred_username": "rahul.reddy@test.example", "name": "Rahul Reddy"}
    claims.update(overrides)
    claims = {k: v for k, v in claims.items() if v is not None}
    return jwt.encode(claims, key, algorithm="RS256")


def start(client, next_path: str | None = "/team"):
    r = client.get("/api/auth/entra/login", params={"next": next_path} if next_path else None, follow_redirects=False)
    assert r.status_code == 302
    query = parse_qs(urlparse(r.headers["location"]).query)
    return r, query["state"][0], query["nonce"][0]


def finish(client, monkeypatch, state: str, token: str, code: str = "auth-code", captured: dict | None = None):
    def fake_post(form):
        if captured is not None:
            captured.update(form)
        return {"id_token": token, "token_type": "Bearer"}

    monkeypatch.setattr(entra, "_post_token", fake_post)
    return client.get("/api/auth/entra/callback", params={"code": code, "state": state}, follow_redirects=False)


def refused(r, code: str):
    assert r.status_code == 302 and r.headers["location"] == f"/login?error={code}", r.headers.get("location")
    assert "ats_session" not in r.cookies


# ------------------------------------------------------------------ happy path

def test_entra_sign_in_links_account_and_starts_session(client, org, db, entra_on, monkeypatch):
    r, state, nonce = start(client, "/team?start=2026-03-01")
    location = r.headers["location"]
    assert location.startswith(f"https://login.microsoftonline.com/{TENANT}/oauth2/v2.0/authorize?")
    q = parse_qs(urlparse(location).query)
    assert q["client_id"] == [CLIENT] and q["code_challenge_method"] == ["S256"] and q["response_type"] == ["code"]
    assert "openid" in q["scope"][0]
    flow_cookie = r.headers["set-cookie"]
    assert "ats_oidc=" in flow_cookie and "HttpOnly" in flow_cookie and "Path=/api/auth/entra" in flow_cookie

    captured: dict = {}
    r = finish(client, monkeypatch, state, id_token(nonce), captured=captured)
    assert r.status_code == 302 and r.headers["location"] == "/team?start=2026-03-01"
    assert captured["code"] == "auth-code" and len(captured["code_verifier"]) >= 43  # PKCE verifier sent
    assert captured["grant_type"] == "authorization_code"

    me = client.get("/api/auth/me")
    assert me.status_code == 200 and me.json()["user"]["email"] == "rahul.reddy@test.example"
    db.expire_all()
    db.refresh(org.rahul)
    assert org.rahul.graph_user_id == OID  # linked on first sign-in
    actions = {(a.action, (a.after_values or {}).get("method")) for a in db.scalars(select(AuditLog))}
    assert ("USER_IDENTITY_LINKED", None) in actions and ("LOGIN", "entra") in actions


def test_second_sign_in_matches_by_object_id_even_if_email_changes(client, org, db, entra_on, monkeypatch):
    org.rahul.graph_user_id = OID
    db.commit()
    _, state, nonce = start(client)
    r = finish(client, monkeypatch, state, id_token(nonce, preferred_username="renamed@test.example"))
    assert r.status_code == 302 and r.headers["location"] == "/team"
    assert client.get("/api/auth/me").json()["user"]["full_name"] == "Rahul Reddy"


# ------------------------------------------------------------------ every refusal

@pytest.mark.parametrize("overrides, code", [
    ({"aud": "someone-else"}, "invalid_token"),
    ({"iss": "https://login.microsoftonline.com/other-tenant/v2.0"}, "invalid_token"),
    ({"tid": "other-tenant"}, "invalid_token"),
    ({"exp": int(time.time()) - 3600, "iat": int(time.time()) - 7200, "nbf": int(time.time()) - 7200}, "invalid_token"),
    ({"oid": None}, "invalid_token"),
    ({"nonce": "replayed-nonce"}, "invalid_token"),
])
def test_bad_id_tokens_are_refused(client, org, entra_on, monkeypatch, overrides, code):
    _, state, nonce = start(client)
    refused(finish(client, monkeypatch, state, id_token(nonce, **overrides)), code)
    assert client.get("/api/auth/me").status_code == 401


def test_token_signed_by_another_key_is_refused(client, org, entra_on, monkeypatch):
    _, state, nonce = start(client)
    refused(finish(client, monkeypatch, state, id_token(nonce, key=OTHER_KEY)), "invalid_token")


def test_state_must_match_the_flow_cookie(client, org, entra_on, monkeypatch):
    _, _state, nonce = start(client)
    refused(finish(client, monkeypatch, "forged-state", id_token(nonce)), "sign_in_expired")


def test_callback_without_flow_cookie_or_with_tampered_cookie(client, org, entra_on, monkeypatch):
    _, state, nonce = start(client)
    token = client.cookies.get("ats_oidc", path="/api/auth/entra")
    client.cookies.set("ats_oidc", token[:-3] + "AAA", path="/api/auth/entra")
    refused(finish(client, monkeypatch, state, id_token(nonce)), "sign_in_expired")
    client.cookies.clear()
    refused(finish(client, monkeypatch, state, id_token(nonce)), "sign_in_expired")


def test_unknown_inactive_and_mismatched_accounts_are_refused(client, org, db, entra_on, monkeypatch):
    _, state, nonce = start(client)
    refused(finish(client, monkeypatch, state, id_token(nonce, preferred_username="stranger@test.example")),
            "no_account")

    org.sneha.is_active = False
    db.commit()
    _, state, nonce = start(client)
    refused(finish(client, monkeypatch, state, id_token(nonce, preferred_username="sneha.rao@test.example",
                                                        oid="99999999-0000-0000-0000-000000000000")),
            "account_inactive")

    org.arjun.graph_user_id = "12345678-0000-0000-0000-000000000000"  # already linked to another identity
    db.commit()
    _, state, nonce = start(client)
    refused(finish(client, monkeypatch, state, id_token(nonce, preferred_username="arjun.kumar@test.example",
                                                        oid="87654321-0000-0000-0000-000000000000")),
            "account_mismatch")
    db.expire_all()
    db.refresh(org.arjun)
    assert org.arjun.graph_user_id == "12345678-0000-0000-0000-000000000000"  # never re-linked


def test_user_cancelling_at_microsoft(client, org, entra_on):
    start(client)
    r = client.get("/api/auth/entra/callback", params={"error": "access_denied", "error_description": "cancelled"},
                   follow_redirects=False)
    refused(r, "entra_denied")


@pytest.mark.parametrize("next_path", ["https://evil.example/", "//evil.example", "/\\evil.example", "javascript:x"])
def test_next_cannot_redirect_off_site(client, org, entra_on, monkeypatch, next_path):
    _, state, nonce = start(client, next_path)
    r = finish(client, monkeypatch, state, id_token(nonce))
    assert r.status_code == 302 and r.headers["location"] == "/"


def test_entra_endpoints_404_when_not_configured(client):
    assert client.get("/api/auth/entra/login", follow_redirects=False).status_code == 404
    assert client.get("/api/auth/entra/callback", follow_redirects=False).status_code == 404


# ------------------------------------------------------------------ providers, password switch, dev login

def test_providers_reflect_configuration(client, entra_on, monkeypatch):
    assert client.get("/api/auth/providers").json() == {"password": True, "entra": True, "dev": False}
    monkeypatch.setattr(entra_on, "password_login_enabled", False)
    monkeypatch.setattr(entra_on, "dev_login_enabled", True)
    assert client.get("/api/auth/providers").json() == {"password": False, "entra": True, "dev": True}


def test_password_login_can_be_turned_off(client, org, monkeypatch):
    monkeypatch.setattr(get_settings(), "password_login_enabled", False)
    r = client.post("/api/auth/login", json={"email": org.rahul.email, "password": "Str0ngPassw0rd!"})
    assert r.status_code == 403 and r.json()["error"]["code"] == "password_login_disabled"


def test_dev_login_is_off_by_default(client, org):
    assert client.post("/api/auth/dev-login", json={"email": org.rahul.email}).status_code == 404


def test_dev_login_when_enabled(client, org, db, monkeypatch):
    monkeypatch.setattr(get_settings(), "dev_login_enabled", True)
    r = client.post("/api/auth/dev-login", json={"email": org.priya.email})
    assert r.status_code == 200 and r.json()["user"]["role"] == "MANAGER"
    assert client.get("/api/team/members").status_code == 200  # a real session with the user's own role
    assert db.scalar(select(AuditLog).where(AuditLog.action == "LOGIN")).after_values == {"method": "dev"}
    org.sneha.is_active = False
    db.commit()
    assert client.post("/api/auth/dev-login", json={"email": org.sneha.email}).status_code == 401
    assert client.post("/api/auth/dev-login", json={"email": "nobody@test.example"}).status_code == 401


def test_password_login_still_works_and_records_method(client, org, db):
    login(client, org.rahul)
    assert db.scalar(select(AuditLog).where(AuditLog.action == "LOGIN")).after_values == {"method": "password"}


# ------------------------------------------------------------------ settings validation at startup

BASE = {"database_url": "sqlite://", "secret_key": "k" * 40, "_env_file": None}
ENTRA = {"entra_tenant_id": TENANT, "entra_client_id": CLIENT, "entra_client_secret": "s" * 32,
         "entra_redirect_uri": "https://ats.example/api/auth/entra/callback"}


def test_dev_login_is_refused_in_production():
    with pytest.raises(ValidationError, match="DEV_LOGIN_ENABLED"):
        Settings(**BASE, app_env="production", dev_login_enabled=True)


def test_partial_entra_configuration_is_refused():
    with pytest.raises(ValidationError, match="ENTRA_TENANT_ID"):
        Settings(**BASE, entra_tenant_id=TENANT)


def test_some_sign_in_method_must_be_enabled():
    with pytest.raises(ValidationError, match="No sign-in method"):
        Settings(**BASE, password_login_enabled=False)
    assert Settings(**BASE, password_login_enabled=False, **ENTRA).entra_enabled


def test_production_entra_redirect_must_be_https():
    with pytest.raises(ValidationError, match="https"):
        Settings(**BASE, app_env="production", **{**ENTRA, "entra_redirect_uri": "http://ats.example/cb"})
