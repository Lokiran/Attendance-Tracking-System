"""Management commands.

    python -m app.cli seed [--reset-demo] [--reset-passwords]
    python -m app.cli create-admin --email admin@company.com --name "Jane Doe" --code ADM100
"""

import argparse
import getpass
import os
import sys

from dotenv import load_dotenv
from sqlalchemy import func, select

from app.core.config import describe_database, get_settings, selected_env_file

# SEED_* values live in the same explicitly selected env file as DATABASE_URL.
load_dotenv(selected_env_file())

from app.core.context import RequestContext  # noqa: E402
from app.core.context import RequestContext  # noqa: E402
from app.core.errors import AppError  # noqa: E402
from app.core.security import hash_password, validate_password_strength  # noqa: E402
from app.core.timeutil import today_local  # noqa: E402
from app.db.session import SessionLocal  # noqa: E402
from app.models import Role, User  # noqa: E402
from app.services import audit  # noqa: E402


def cmd_seed(args) -> int:
    from app.seed import SeedError, run

    with SessionLocal() as db:
        try:
            result = run(db, reset_demo=args.reset_demo, reset_passwords=args.reset_passwords)
        except (SeedError, AppError) as exc:
            print(f"Seed failed: {getattr(exc, 'message', exc)}", file=sys.stderr)
            return 1
    print(f"Seed complete: {result}")
    return 0


def cmd_create_admin(args) -> int:
    password = os.environ.get("ATS_ADMIN_PASSWORD") or getpass.getpass("New admin password: ")
    if not os.environ.get("ATS_ADMIN_PASSWORD") and getpass.getpass("Repeat password: ") != password:
        print("Passwords do not match", file=sys.stderr)
        return 1
    try:
        validate_password_strength(password)
    except AppError as exc:
        print(exc.message, file=sys.stderr)
        return 1
    email = args.email.strip().lower()
    with SessionLocal() as db:
        if db.scalar(select(User.id).where((User.email == email) |
                                           (func.upper(User.employee_code) == args.code.upper()))):
            print("A user with this email or employee code already exists", file=sys.stderr)
            return 1
        user = User(employee_code=args.code.upper(), full_name=args.name, email=email,
                    password_hash=hash_password(password), role=Role.ADMIN, is_active=True,
                    attendance_tracked=False, date_joined=today_local(), job_title="Administrator")
        db.add(user)
        db.flush()
        audit.record(db, actor_id=None, action="ADMIN_BOOTSTRAPPED", entity_type="user", entity_id=user.id,
                     after={"email": email, "employee_code": user.employee_code},
                     ctx=RequestContext(request_id="cli"))
        db.commit()
    print(f"Administrator {email} created")
    return 0


def cmd_wfo_refresh(args) -> int:
    """Recompute and store WFO classification for a date range (after schedule / holiday changes)."""
    from datetime import date

    from sqlalchemy import select

    from app.db.session import SessionLocal
    from app.models import User
    from app.services import wfo
    start, end = date.fromisoformat(args.start), date.fromisoformat(args.end)
    if end < start:
        print("--to must be on or after --from", file=sys.stderr)
        return 2
    with SessionLocal() as db:
        people = list(db.scalars(select(User).where(User.attendance_tracked.is_(True))).unique())
        written = wfo.refresh_range(db, people, start, end)
        db.commit()
    print(f"WFO refreshed for {len(people)} people, {start} to {end}: {written} day record(s) written")
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="python -m app.cli")
    sub = parser.add_subparsers(dest="command", required=True)
    seed = sub.add_parser("seed", help="Load fictional demo data (development only)")
    seed.add_argument("--reset-demo", action="store_true", help="Regenerate demo attendance relative to today")
    seed.add_argument("--reset-passwords", action="store_true", help="Reset demo users' passwords from env")
    seed.set_defaults(func=cmd_seed)
    admin = sub.add_parser("create-admin", help="Create an administrator (prompts for the password)")
    admin.add_argument("--email", required=True)
    admin.add_argument("--name", required=True)
    admin.add_argument("--code", required=True, help="Employee code, e.g. ADM100")
    admin.set_defaults(func=cmd_create_admin)
    refresh = sub.add_parser("wfo-refresh", help="Recompute WFO work modes for a date range")
    refresh.add_argument("--from", dest="start", required=True, help="YYYY-MM-DD")
    refresh.add_argument("--to", dest="end", required=True, help="YYYY-MM-DD")
    refresh.set_defaults(func=cmd_wfo_refresh)
    args = parser.parse_args(argv)
    from app.db.base import Base
    from app.db.session import DatabaseNotReady, ensure_sqlite_ready, engine

    print(f"Database: {describe_database(get_settings().database_url)}  (env file: {selected_env_file()})")
    try:
        ensure_sqlite_ready(engine, set(Base.metadata.tables))
    except DatabaseNotReady as exc:
        print(exc, file=sys.stderr)
        return 1
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
