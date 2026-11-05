"""Connectivity probe used by test-postgres.ps1: prints server version and btree_gist availability."""

import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backend"))

from sqlalchemy import create_engine, text  # noqa: E402

from app.core.config import normalize_database_url  # noqa: E402

url = normalize_database_url(os.environ["TEST_DATABASE_URL"])
try:
    with create_engine(url, connect_args={"connect_timeout": 5}).connect() as conn:
        print("server:", conn.execute(text("SHOW server_version")).scalar())
        available = conn.execute(text("SELECT 1 FROM pg_available_extensions WHERE name = 'btree_gist'")).scalar()
        print("btree_gist available:", bool(available))
except Exception as exc:  # report the reason without a traceback
    sys.exit(f"Cannot connect to PostgreSQL: {exc.__class__.__name__}: {str(exc).splitlines()[0]}")
