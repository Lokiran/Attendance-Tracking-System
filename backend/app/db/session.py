from collections.abc import Iterator
from pathlib import Path

from sqlalchemy import create_engine, event, inspect
from sqlalchemy.engine import Engine, make_url
from sqlalchemy.orm import Session, sessionmaker

from app.core.config import get_settings


def build_engine(url: str) -> Engine:
    kwargs: dict = {"pool_pre_ping": True}
    if url.startswith("postgresql"):
        # Fail fast (-> HTTP 503) instead of hanging when the server is unreachable.
        kwargs["connect_args"] = {"connect_timeout": 10}
    elif url.startswith("sqlite"):
        # Threadpool requests share the file; wait for a writer instead of failing immediately.
        kwargs = {"connect_args": {"check_same_thread": False, "timeout": 15}}
    engine = create_engine(url, **kwargs)
    if engine.dialect.name == "sqlite":
        @event.listens_for(engine, "connect")
        def _fk_on(dbapi_conn, _):  # pragma: no cover - sqlite only
            cur = dbapi_conn.cursor()
            cur.execute("PRAGMA foreign_keys=ON")
            cur.close()
    return engine


engine = build_engine(get_settings().database_url)
SessionLocal = sessionmaker(bind=engine, autoflush=False, expire_on_commit=False)


class DatabaseNotReady(RuntimeError):
    pass


def ensure_sqlite_ready(db_engine: Engine, required_tables: set[str]) -> None:
    """Refuse to start on a missing or uninitialised SQLite file.

    Connecting to a missing SQLite file would silently create an empty database, so the file is
    checked on disk first. The app never creates or migrates a database implicitly.
    PostgreSQL is not checked here: if it is unreachable, requests answer 503 (no fallback).
    """
    url = make_url(str(db_engine.url))
    if url.get_backend_name() != "sqlite" or url.database in (None, "", ":memory:"):
        return
    path = Path(url.database)
    hint = ("Initialise it explicitly: cd backend; $env:ATS_ENV_FILE='.env.sqlite'; "
            ".\\.venv\\Scripts\\alembic.exe upgrade head   (or scripts\\run-local-sqlite.ps1 -Init)")
    if not path.is_file():
        raise DatabaseNotReady(f"SQLite database file not found: {path}. {hint}")
    missing = required_tables - set(inspect(db_engine).get_table_names())
    if missing:
        raise DatabaseNotReady(f"SQLite database {path} has no application schema "
                               f"(missing {', '.join(sorted(missing))}). {hint}")


def get_db() -> Iterator[Session]:
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
