"""Run Alembic programmatically against the test database."""

import io
from contextlib import redirect_stdout
from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy import inspect, text
from sqlalchemy.engine import Engine

from app.db.base import Base

BACKEND_DIR = Path(__file__).resolve().parents[2]


def alembic_config(url: str | None = None) -> Config:
    # No ini file: alembic/env.py then skips logging.fileConfig (which would reset pytest's logging)
    # and takes the URL from app settings, i.e. the test database.
    cfg = Config()
    cfg.set_main_option("script_location", str(BACKEND_DIR / "alembic"))
    if url:
        cfg.attributes["sqlalchemy.url"] = url
    return cfg


def reset_schema(engine: Engine) -> None:
    """Drop every application table and the Alembic version table."""
    Base.metadata.drop_all(engine)
    with engine.begin() as conn:
        conn.execute(text("DROP TABLE IF EXISTS alembic_version"))


def upgrade(revision: str = "head") -> None:
    command.upgrade(alembic_config(), revision)


def downgrade(revision: str = "base") -> None:
    command.downgrade(alembic_config(), revision)


def current_revision(engine: Engine) -> str | None:
    if "alembic_version" not in inspect(engine).get_table_names():
        return None
    with engine.connect() as conn:
        return conn.execute(text("SELECT version_num FROM alembic_version")).scalar()


def offline_sql(url: str, revision: str = "head", downgrade_from: str | None = None) -> str:
    """Render the migration as SQL for the given dialect without connecting (alembic --sql)."""
    cfg = alembic_config(url)
    buf = io.StringIO()
    cfg.output_buffer = buf
    with redirect_stdout(io.StringIO()):
        if downgrade_from:
            command.downgrade(cfg, f"{downgrade_from}:{revision}", sql=True)
        else:
            command.upgrade(cfg, revision, sql=True)
    return buf.getvalue()


def app_tables() -> set[str]:
    return set(Base.metadata.tables)
