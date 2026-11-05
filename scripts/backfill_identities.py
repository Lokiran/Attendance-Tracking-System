r"""Backfill users.graph_user_id / users.wifi_identity from a CSV (dry run unless --apply).

Usage (PowerShell, from the repo root):
    $env:ATS_ENV_FILE = ".env.sqlite"          # or leave unset for backend\.env (PostgreSQL)
    backend\.venv\Scripts\python.exe scripts\backfill_identities.py identities.csv
    backend\.venv\Scripts\python.exe scripts\backfill_identities.py identities.csv --apply

CSV header: employee_code (or email), graph_user_id, wifi_identity. Blank cells leave values unchanged.
Every change is written to the audit log as USER_IDENTITY_LINKED.
"""

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backend"))

from app.core.config import describe_database, get_settings  # noqa: E402
from app.core.context import RequestContext  # noqa: E402
from app.db.session import SessionLocal  # noqa: E402
from app.services.identities import apply_plan, plan_from_csv  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("csv", type=Path)
    parser.add_argument("--apply", action="store_true", help="write the changes (default: dry run)")
    args = parser.parse_args()

    print(f"Database: {describe_database(get_settings().database_url)}")
    with SessionLocal() as db:
        plan = plan_from_csv(db, args.csv.read_text(encoding="utf-8"))
        for change in plan.changes:
            diff = {k: f"{change.before[k]} -> {change.after[k]}" for k in change.after if change.before[k] != change.after[k]}
            print(f"  {change.employee_code}: {diff}")
        print(f"{len(plan.changes)} change(s), {plan.unchanged} unchanged, {len(plan.errors)} error(s)")
        for err in plan.errors:
            print(f"  ERROR {err}", file=sys.stderr)
        if plan.errors:
            print("Nothing was written.", file=sys.stderr)
            return 1
        if not args.apply:
            print("Dry run: re-run with --apply to write these changes.")
            return 0
        applied = apply_plan(db, plan, actor_id=None, ctx=RequestContext(request_id="backfill-identities"))
        print(f"Applied {applied} change(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
