r"""Copy every table of the local SQLite database into an empty PostgreSQL database.

Usage (PowerShell, from the repository root; both databases already migrated to the same revision):
    backend\.venv\Scripts\python.exe scripts\copy_sqlite_to_postgres.py            # check only
    backend\.venv\Scripts\python.exe scripts\copy_sqlite_to_postgres.py --apply    # copy

Source: the SQLite file in backend\.env.sqlite. Target: DATABASE_URL in backend\.env (PostgreSQL).

Safety:
* The source is only read. The target must be PostgreSQL, at the same Alembic revision, and EMPTY
  (every application table); otherwise nothing is written.
* One transaction: on any error nothing is kept. Ids are kept as they are (links stay intact), then
  each id sequence is moved past the highest id. Row counts are compared at the end.
* Connection details are never printed.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

BACKEND = Path(__file__).resolve().parents[1] / "backend"
sys.path.insert(0, str(BACKEND))
os.chdir(BACKEND)


def _env_value(file: Path, key: str) -> str:
    for line in file.read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    raise SystemExit(f"{key} is not set in {file}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--apply", action="store_true", help="copy (default: check only)")
    args = parser.parse_args()

    # Settings for the app modules: the PostgreSQL profile (backend/.env) is the target.
    os.environ.pop("ATS_ENV_FILE", None)
    from sqlalchemy import create_engine, func, inspect, select, text

    import app.models  # noqa: F401  (registers every table)
    from app.core.config import describe_database, normalize_database_url
    from app.db.base import Base

    source_url = normalize_database_url(_env_value(BACKEND / ".env.sqlite", "DATABASE_URL"))
    target_url = normalize_database_url(_env_value(BACKEND / ".env", "DATABASE_URL"))
    if not source_url.startswith("sqlite"):
        raise SystemExit("backend/.env.sqlite does not point at a SQLite file")
    if not target_url.startswith("postgresql"):
        raise SystemExit("backend/.env does not point at PostgreSQL")
    print(f"source: {describe_database(source_url)}")
    print(f"target: {describe_database(target_url)}")
    src, dst = create_engine(source_url), create_engine(target_url)

    def revision(engine) -> str | None:
        with engine.connect() as c:
            return c.execute(text("SELECT version_num FROM alembic_version")).scalar()

    if revision(src) != revision(dst):
        raise SystemExit(f"revisions differ (source {revision(src)}, target {revision(dst)}): migrate both first")
    tables = [t for t in Base.metadata.sorted_tables if t.name in set(inspect(src).get_table_names())]

    with src.connect() as s, dst.connect() as d:
        counts = {t.name: s.execute(select(func.count()).select_from(t)).scalar() for t in tables}
        busy = {t.name: n for t in tables if (n := d.execute(select(func.count()).select_from(t)).scalar())}
    if busy:
        raise SystemExit(f"the target is not empty ({busy}): nothing copied")
    for name, n in counts.items():
        if n:
            print(f"  {name:32} {n:>7}")
    print(f"  {'total rows':32} {sum(counts.values()):>7}")
    if not args.apply:
        print("check only: run again with --apply to copy")
        return 0

    with src.connect() as s, dst.begin() as d:
        inserted: set[str] = set()
        for table in tables:
            rows = [dict(r._mapping) for r in s.execute(select(table))]
            # A link to a row not copied yet (a table's own manager, a department head) is set afterwards.
            later = [fk.parent.name for fk in table.foreign_keys
                     if fk.column.table.name == table.name or fk.column.table.name not in inserted]
            pending = []
            for row in rows:
                held = {c: row[c] for c in later if row.get(c) is not None}
                if held:
                    pending.append(({k: row[k] for k in table.primary_key.columns.keys()}, held))
                    row.update({c: None for c in held})
            for start in range(0, len(rows), 1000):
                d.execute(table.insert(), rows[start:start + 1000])
            for key, held in pending:
                d.execute(table.update().where(*[table.c[k] == v for k, v in key.items()]).values(**held))
            inserted.add(table.name)
        for table in tables:
            pk = list(table.primary_key.columns)
            if len(pk) == 1 and pk[0].autoincrement is not False and pk[0].type.python_type is int:
                d.execute(text(f"SELECT setval(pg_get_serial_sequence('{table.name}', '{pk[0].name}'), "
                               f"COALESCE((SELECT MAX({pk[0].name}) FROM {table.name}), 0) + 1, false)"))
        copied = {t.name: d.execute(select(func.count()).select_from(t)).scalar() for t in tables}
        if copied != counts:
            raise SystemExit("row counts differ after copying: rolled back, nothing kept")
    print(f"copied {sum(counts.values())} rows in {sum(1 for n in counts.values() if n)} tables; counts match")
    return 0


if __name__ == "__main__":
    sys.exit(main())
