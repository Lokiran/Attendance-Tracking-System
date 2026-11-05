"""Database selection must be explicit: no default database, no silent fallback, no implicit creation."""

import sqlite3
from pathlib import Path

import pytest
from sqlalchemy import create_engine

from app.core.config import BACKEND_DIR, Settings, describe_database, normalize_database_url, selected_env_file
from app.db.base import Base
from app.db.session import DatabaseNotReady, ensure_sqlite_ready

TABLES = set(Base.metadata.tables)


def test_database_url_is_required(monkeypatch):
    monkeypatch.delenv("DATABASE_URL", raising=False)
    with pytest.raises(ValueError, match="DATABASE_URL is not set and there is no default database"):
        Settings(_env_file=None)


def test_postgres_url_is_kept_as_postgres_never_replaced_by_sqlite():
    s = Settings(database_url="postgresql://ats:pw@db.example:5432/ats", secret_key="k" * 40, _env_file=None)
    assert s.database_url == "postgresql+psycopg://ats:pw@db.example:5432/ats"


def test_relative_sqlite_path_is_anchored_to_backend_dir():
    url = normalize_database_url("sqlite:///data/ats-dev.sqlite3")
    assert url == "sqlite:///" + (BACKEND_DIR / "data" / "ats-dev.sqlite3").resolve().as_posix()


def test_env_file_defaults_to_backend_dotenv(monkeypatch):
    monkeypatch.delenv("ATS_ENV_FILE", raising=False)
    assert selected_env_file() == BACKEND_DIR / ".env"


def test_explicit_env_file_must_exist(monkeypatch, tmp_path):
    monkeypatch.setenv("ATS_ENV_FILE", str(tmp_path / "missing.env"))
    with pytest.raises(RuntimeError, match="does not exist"):
        selected_env_file()
    present = tmp_path / "profile.env"
    present.write_text("DATABASE_URL=sqlite:///x.db\n")
    monkeypatch.setenv("ATS_ENV_FILE", str(present))
    assert selected_env_file() == present


def test_env_file_selection_loads_its_database(monkeypatch, tmp_path):
    profile = tmp_path / "local.env"
    profile.write_text(f"DATABASE_URL=sqlite:///{(tmp_path / 'dev.sqlite3').as_posix()}\nSECRET_KEY={'k' * 40}\n")
    monkeypatch.delenv("DATABASE_URL", raising=False)
    s = Settings(_env_file=profile)
    assert s.database_url.endswith("/dev.sqlite3")


def test_describe_database_hides_credentials():
    text = describe_database("postgresql+psycopg://ats:supersecret@db.example:5432/ats")
    assert "supersecret" not in text and "db.example" in text
    assert describe_database("sqlite:///C:/x/dev.sqlite3").startswith("SQLite file")


def test_missing_sqlite_file_is_refused_and_not_created(tmp_path):
    path = tmp_path / "nope.sqlite3"
    engine = create_engine(f"sqlite:///{path.as_posix()}")
    with pytest.raises(DatabaseNotReady, match="not found"):
        ensure_sqlite_ready(engine, TABLES)
    assert not path.exists()


def test_uninitialised_sqlite_file_is_refused(tmp_path):
    path = tmp_path / "empty.sqlite3"
    sqlite3.connect(path).close()
    engine = create_engine(f"sqlite:///{path.as_posix()}")
    with pytest.raises(DatabaseNotReady, match="no application schema"):
        ensure_sqlite_ready(engine, TABLES)


def test_initialised_sqlite_file_is_accepted(tmp_path):
    path = tmp_path / "ok.sqlite3"
    engine = create_engine(f"sqlite:///{path.as_posix()}")
    Base.metadata.create_all(engine)
    ensure_sqlite_ready(engine, TABLES)


def test_postgres_is_not_probed_at_startup():
    # No connection is attempted for PostgreSQL; an unreachable server yields 503s, never a fallback.
    ensure_sqlite_ready(create_engine("postgresql+psycopg://u:p@203.0.113.1:5432/x_test"), TABLES)


def test_sqlite_env_template_is_explicit():
    template = (BACKEND_DIR / ".env.sqlite.example").read_text()
    assert "DATABASE_URL=sqlite:///data/ats-dev.sqlite3" in template
    assert "postgresql" not in template.split("DATABASE_URL=")[1].splitlines()[0]
    deploy = (BACKEND_DIR / ".env.example").read_text()
    assert "DATABASE_URL=postgresql+psycopg://" in deploy
    assert Path(BACKEND_DIR / ".env.sqlite.example").is_file()
