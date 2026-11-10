from datetime import timedelta

from sqlalchemy import select

from app.models import AuditLog, UserSession
from app.tests.conftest import PASSWORD, login


def test_login_success_sets_httponly_cookie_and_hides_secrets(client, org):
    r = client.post("/api/auth/login", json={"email": org.rahul.email, "password": PASSWORD})
    assert r.status_code == 200
    body = r.json()
    assert body["user"]["full_name"] == "Rahul Reddy"
    assert body["user"]["manager"]["full_name"] == "Priya Sharma"
    assert "password_hash" not in r.text and PASSWORD not in r.text
    set_cookie = r.headers.get_list("set-cookie")
    session_cookie = next(c for c in set_cookie if c.startswith("ats_session="))
    assert "HttpOnly" in session_cookie and "SameSite=lax" in session_cookie
    csrf_cookie = next(c for c in set_cookie if c.startswith("ats_csrf="))
    assert "HttpOnly" not in csrf_cookie


def test_login_is_case_insensitive_on_email(client, org):
    r = client.post("/api/auth/login", json={"email": org.rahul.email.upper(), "password": PASSWORD})
    assert r.status_code == 200


def test_login_failure_is_generic(client, org):
    wrong = client.post("/api/auth/login", json={"email": org.rahul.email, "password": "nope-nope-1"})
    missing = client.post("/api/auth/login", json={"email": "ghost@test.example", "password": "nope-nope-1"})
    assert wrong.status_code == missing.status_code == 401
    assert wrong.json()["error"]["message"] == missing.json()["error"]["message"] == "Invalid email or password"


def test_inactive_user_cannot_login(client, factory, org):
    u = factory.user("Gone Person", active=False)
    r = client.post("/api/auth/login", json={"email": u.email, "password": PASSWORD})
    assert r.status_code == 401


def test_login_rate_limited_after_repeated_failures(client, org):
    for _ in range(5):
        client.post("/api/auth/login", json={"email": org.rahul.email, "password": "bad-password-1"})
    r = client.post("/api/auth/login", json={"email": org.rahul.email, "password": PASSWORD})
    assert r.status_code == 429
    assert "Retry-After" in r.headers


def test_rate_limit_window_expires(client, frozen, org):
    for _ in range(5):
        client.post("/api/auth/login", json={"email": org.rahul.email, "password": "bad-password-1"})
    frozen.advance(minutes=16)
    assert client.post("/api/auth/login", json={"email": org.rahul.email, "password": PASSWORD}).status_code == 200


def test_me_requires_authentication(client, org):
    r = client.get("/api/auth/me")
    assert r.status_code == 401
    assert r.json()["error"]["code"] == "not_authenticated"


def test_logout_revokes_session(client, org, db):
    headers = login(client, org.rahul)
    assert client.get("/api/auth/me").status_code == 200
    token = client.cookies.get("ats_session")
    assert client.post("/api/auth/logout", headers=headers).status_code == 200
    client.cookies.set("ats_session", token)  # replaying the old cookie must fail
    assert client.get("/api/auth/me").status_code == 401
    assert db.scalar(select(UserSession).where(UserSession.user_id == org.rahul.id)).revoked_at is not None


def test_state_changing_request_requires_csrf_token(client, org):
    login(client, org.rahul)
    r = client.post("/api/auth/logout", json={})
    assert r.status_code == 403 and r.json()["error"]["code"] == "csrf_failed"
    r = client.post("/api/auth/logout", json={}, headers={"X-CSRF-Token": "forged"})
    assert r.status_code == 403
    assert client.get("/api/auth/me").status_code == 200  # still signed in


def test_session_idle_expiry(client, frozen, org):
    login(client, org.rahul)
    frozen.advance(minutes=121)
    r = client.get("/api/auth/me")
    assert r.status_code == 401 and r.json()["error"]["code"] == "session_expired"


def test_session_absolute_expiry(client, frozen, org):
    login(client, org.rahul)
    for _ in range(13):  # keep it active, but exceed the 12h absolute lifetime
        frozen.advance(minutes=60)
        client.get("/api/auth/me")
    assert client.get("/api/auth/me").status_code == 401


def test_session_token_stored_hashed(client, org, db):
    login(client, org.rahul)
    token = client.cookies.get("ats_session")
    stored = db.scalar(select(UserSession.token_hash))
    assert token and stored != token and len(stored) == 64


def test_change_password_and_audit(client, org, db):
    headers = login(client, org.rahul)
    r = client.post("/api/auth/change-password", headers=headers,
                    json={"current_password": PASSWORD, "new_password": "An0therStrongOne"})
    assert r.status_code == 200
    client.post("/api/auth/logout", headers=headers)
    assert client.post("/api/auth/login", json={"email": org.rahul.email, "password": PASSWORD}).status_code == 401
    assert client.post("/api/auth/login",
                       json={"email": org.rahul.email, "password": "An0therStrongOne"}).status_code == 200
    log = db.scalar(select(AuditLog).where(AuditLog.action == "PASSWORD_CHANGED"))
    assert log is not None and "An0ther" not in str(log.after_values) + str(log.before_values)


def test_weak_password_rejected(client, org):
    headers = login(client, org.rahul)
    r = client.post("/api/auth/change-password", headers=headers,
                    json={"current_password": PASSWORD, "new_password": "short"})
    assert r.status_code == 422


def test_health_endpoints(client):
    assert client.get("/api/health").json()["status"] == "ok"
    assert client.get("/api/health/db").json()["status"] == "ok"


def test_unknown_route_returns_json_error(client):
    r = client.get("/api/does-not-exist")
    assert r.status_code == 404 and r.json()["error"]["code"] == "not_found"


def test_security_headers(client):
    r = client.get("/api/health")
    assert r.headers["X-Content-Type-Options"] == "nosniff"
    assert r.headers["X-Frame-Options"] == "DENY"
    assert r.headers["X-Request-ID"]


def test_cors_rejects_unknown_origin(client):
    r = client.options("/api/auth/login", headers={"Origin": "https://evil.example",
                                                   "Access-Control-Request-Method": "POST"})
    assert r.headers.get("access-control-allow-origin") is None
    ok = client.options("/api/auth/login", headers={"Origin": "http://localhost:5173",
                                                    "Access-Control-Request-Method": "POST"})
    assert ok.headers.get("access-control-allow-origin") == "http://localhost:5173"


def test_production_settings_require_strong_secret(monkeypatch):
    import pytest

    from app.core.config import Settings
    with pytest.raises(ValueError):
        Settings(app_env="production", secret_key="change-me", _env_file=None)
    with pytest.raises(ValueError):
        Settings(app_env="production", secret_key="x" * 40, cookie_secure=False, _env_file=None)
    ok = Settings(app_env="production", secret_key="q8V2nL0pZr7sX1cB4mK9tY6wE3uH5jA0", _env_file=None)
    assert ok.secure_cookies is True
    assert timedelta(hours=ok.session_absolute_hours)
