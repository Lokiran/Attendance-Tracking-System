"""Application settings loaded from environment variables and one explicitly selected env file.

Env file: ``$ATS_ENV_FILE`` if set (absolute, or relative to ``backend/``), otherwise ``backend/.env``.
It is resolved from this file's location, never from the current working directory. There is no
default database: ``DATABASE_URL`` must be configured, so the app never silently picks PostgreSQL
or SQLite on its own.
"""

import logging
import os
import secrets
from functools import lru_cache
from pathlib import Path
from typing import Any, Literal
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from pydantic import field_validator, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict
from sqlalchemy.engine import make_url

logger = logging.getLogger("ats.config")

BACKEND_DIR = Path(__file__).resolve().parents[2]
_INSECURE_SECRET_MARKERS = ("change_me", "change-me", "changeme", "secret", "password", "example")


def selected_env_file() -> Path:
    """The env file to load. An explicitly requested file that does not exist is an error."""
    raw = os.environ.get("ATS_ENV_FILE", "").strip()
    if not raw:
        return BACKEND_DIR / ".env"
    path = Path(raw)
    path = path if path.is_absolute() else BACKEND_DIR / path
    if not path.is_file():
        raise RuntimeError(f"ATS_ENV_FILE points to '{path}', which does not exist")
    return path


def normalize_database_url(url: str) -> str:
    """Make the database URL explicit and independent of the working directory.

    * PostgreSQL URLs without a driver use psycopg 3 (the only driver installed); SQLAlchemy would
      otherwise pick psycopg2 for ``postgresql://`` and does not recognise ``postgres://``.
    * Relative SQLite file paths are resolved against ``backend/`` rather than the current directory.
    """
    for prefix in ("postgres://", "postgresql://"):
        if url.startswith(prefix):
            return "postgresql+psycopg://" + url[len(prefix):]
    if url.startswith("sqlite"):
        db = make_url(url).database
        if db and db != ":memory:" and not db.startswith("file:") and not Path(db).is_absolute():
            # Rebuild by hand: render_as_string() would percent-encode the Windows drive colon.
            scheme, _, rest = url.partition(":///")
            query = rest.partition("?")[2]
            return f"{scheme}:///{(BACKEND_DIR / db).resolve().as_posix()}" + (f"?{query}" if query else "")
    return url


def describe_database(url: str) -> str:
    """Human-readable database target for logs, without credentials."""
    parsed = make_url(url)
    if parsed.get_backend_name() == "sqlite":
        return f"SQLite file {Path(parsed.database)}" if parsed.database not in (None, "", ":memory:") \
            else "SQLite in-memory database"
    return f"{parsed.get_backend_name()} {parsed.username or ''}@{parsed.host}:{parsed.port or ''}/{parsed.database}"


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=BACKEND_DIR / ".env", env_file_encoding="utf-8", extra="ignore")

    app_env: Literal["development", "test", "production"] = "development"
    app_name: str = "Office Attendance Tracker"

    database_url: str  # required; see module docstring
    secret_key: str = ""

    office_timezone: str = "Asia/Kolkata"

    session_idle_minutes: int = 120
    session_absolute_hours: int = 12
    session_cookie_name: str = "ats_session"
    csrf_cookie_name: str = "ats_csrf"
    csrf_header_name: str = "X-CSRF-Token"
    cookie_secure: bool | None = None
    cookie_samesite: Literal["lax", "strict", "none"] = "lax"
    cookie_domain: str | None = None

    cors_origins: str = "http://localhost:5173"

    login_max_failures: int = 5
    login_window_minutes: int = 15
    login_ip_max_failures: int = 30
    trust_proxy_headers: bool = False

    max_report_days: int = 366
    log_level: str = "INFO"

    @model_validator(mode="before")
    @classmethod
    def _require_database_url(cls, data: Any) -> Any:
        if isinstance(data, dict) and not str(data.get("database_url") or "").strip():
            raise ValueError(
                "DATABASE_URL is not set and there is no default database. Configure backend/.env "
                "(PostgreSQL), or use the local SQLite profile: ATS_ENV_FILE=.env.sqlite (see README)."
            )
        return data

    @field_validator("database_url")
    @classmethod
    def _explicit_url(cls, url: str) -> str:
        return normalize_database_url(url.strip())

    @model_validator(mode="after")
    def _validate(self) -> "Settings":
        try:
            ZoneInfo(self.office_timezone)
        except (ZoneInfoNotFoundError, ValueError) as exc:
            raise ValueError(f"OFFICE_TIMEZONE '{self.office_timezone}' is not a valid IANA timezone") from exc

        if self.app_env == "production":
            lowered = self.secret_key.lower()
            if len(self.secret_key) < 32 or any(m in lowered for m in _INSECURE_SECRET_MARKERS):
                raise ValueError("SECRET_KEY must be a random value of at least 32 characters in production")
            if self.cookie_secure is False:
                raise ValueError("COOKIE_SECURE cannot be disabled in production")
            if "*" in self.cors_origins:
                raise ValueError("Wildcard CORS origins are not allowed in production")
        elif not self.secret_key:
            # Development convenience only: sessions will not survive a restart.
            logger.warning("SECRET_KEY is not set; using an ephemeral key (sessions reset on restart)")
            self.secret_key = secrets.token_urlsafe(48)

        if self.cookie_samesite == "none" and not self.secure_cookies:
            raise ValueError("COOKIE_SAMESITE=none requires secure cookies")
        return self

    @property
    def secure_cookies(self) -> bool:
        if self.cookie_secure is None:
            return self.app_env == "production"
        return self.cookie_secure

    @property
    def cors_origin_list(self) -> list[str]:
        return [o.strip() for o in self.cors_origins.split(",") if o.strip()]

    @property
    def tz(self) -> ZoneInfo:
        return ZoneInfo(self.office_timezone)


@lru_cache
def get_settings() -> Settings:
    return Settings(_env_file=selected_env_file())
