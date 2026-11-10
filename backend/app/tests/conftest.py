"""Test fixtures.

Two kinds of tests live here:

* **Portable tests** (everything outside ``postgres/``) run on whichever database is configured:
  a temporary SQLite file by default, or PostgreSQL when ``TEST_DATABASE_URL`` is set.
* **PostgreSQL integration tests** (``app/tests/postgres/``, marker ``postgres``) need a real
  PostgreSQL server. Without ``TEST_DATABASE_URL`` they are reported as SKIPPED, never as passed.
  ``pytest --require-postgres`` turns a missing PostgreSQL into an error instead.

``TEST_DATABASE_URL`` must point at a disposable database: every test wipes it. As a safeguard
the database name must contain "test" (override with ATS_ALLOW_DB_WIPE=1).
"""

import os
import tempfile
from datetime import UTC, date, datetime, time
from pathlib import Path

_tmp = Path(tempfile.mkdtemp(prefix="ats-tests-"))
_TEST_DB_URL = os.environ.get("TEST_DATABASE_URL")
os.environ["APP_ENV"] = "test"
os.environ["DATABASE_URL"] = _TEST_DB_URL or f"sqlite:///{_tmp / 'test.db'}"
os.environ["SECRET_KEY"] = "test-only-key-not-for-production-use-0123456789"
os.environ["OFFICE_TIMEZONE"] = "Asia/Kolkata"
os.environ["CORS_ORIGINS"] = "http://localhost:5173"

import pytest  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402
from sqlalchemy import text  # noqa: E402

import app.models  # noqa: E402,F401
from app.core import clock  # noqa: E402
from app.core.config import get_settings  # noqa: E402
from app.core.security import hash_password  # noqa: E402
from app.core.timeutil import local_to_utc  # noqa: E402
from app.db.base import Base  # noqa: E402
from app.db.session import SessionLocal, engine  # noqa: E402
from app.main import app  # noqa: E402
from app.models import AttendancePolicy, Department, Role, User, WorkSchedule  # noqa: E402
from app.services import attendance_data, record_cache  # noqa: E402

PASSWORD = "Str0ngPassw0rd!"
IST = get_settings().tz
_PW_HASH = hash_password(PASSWORD)  # hashing once keeps the suite fast


def is_postgres() -> bool:
    return engine.dialect.name == "postgresql"


PG_SKIP_REASON = ("PostgreSQL integration test: set TEST_DATABASE_URL to a disposable PostgreSQL database "
                  "(e.g. postgresql+psycopg://ats:<password>@localhost:5432/ats_test)")


def pytest_addoption(parser):
    parser.addoption("--require-postgres", action="store_true",
                     help="Fail instead of skipping when TEST_DATABASE_URL is not a PostgreSQL database")


def pytest_configure(config):
    config.addinivalue_line("markers", "postgres: integration test that requires a real PostgreSQL server")
    if (config.getoption("--require-postgres") or os.environ.get("ATS_REQUIRE_POSTGRES") == "1") \
            and not is_postgres():
        pytest.exit("--require-postgres given but TEST_DATABASE_URL is not a PostgreSQL URL", returncode=4)
    if is_postgres() and os.environ.get("ATS_ALLOW_DB_WIPE") != "1" \
            and "test" not in (engine.url.database or "").lower():
        pytest.exit(f"Refusing to wipe database '{engine.url.database}': its name must contain 'test' "
                    "(or set ATS_ALLOW_DB_WIPE=1)", returncode=4)


def pytest_report_header(config):
    url = engine.url
    if is_postgres():
        return [f"database: PostgreSQL {url.host}:{url.port}/{url.database} (driver {engine.dialect.driver}) "
                "- portable AND PostgreSQL integration tests run"]
    return ["database: SQLite (temporary file) - portable tests only; PostgreSQL integration tests are SKIPPED"]


def pytest_collection_modifyitems(config, items):
    pg_dir = Path(__file__).parent / "postgres"
    for item in items:
        if pg_dir in item.path.parents:
            item.add_marker(pytest.mark.postgres)
        if item.get_closest_marker("postgres") and not is_postgres():
            item.add_marker(pytest.mark.skip(reason=PG_SKIP_REASON))


@pytest.fixture(autouse=True)
def _fresh_db():
    if is_postgres():
        Base.metadata.create_all(engine)
        names = ", ".join(t.name for t in Base.metadata.sorted_tables)
        with engine.begin() as conn:
            conn.execute(text(f"TRUNCATE {names} RESTART IDENTITY CASCADE"))
    else:
        Base.metadata.drop_all(engine)
        Base.metadata.create_all(engine)
    # Fresh databases reuse ids and the frozen clock reuses timestamps, so cached results from one
    # test must never be visible in the next.
    record_cache.clear()
    yield


@pytest.fixture(autouse=True)
def _cache_every_scope(monkeypatch):
    """Route even the small test organisations through the dashboard record cache, so every test that
    writes and then reads attendance also proves the cache never serves stale numbers."""
    monkeypatch.setattr(attendance_data, "CACHE_MIN_EMPLOYEES", 0)


class FrozenClock:
    def __init__(self, dt: datetime):
        self.now = dt

    def set_local(self, d: date, t: time) -> None:
        self.now = local_to_utc(d, t, IST)

    def advance(self, **kwargs) -> None:
        from datetime import timedelta
        self.now = self.now + timedelta(**kwargs)

    def __call__(self) -> datetime:
        return self.now


# Tuesday 10 March 2026, 12:00 IST
DEFAULT_NOW = datetime(2026, 3, 10, 6, 30, tzinfo=UTC)


@pytest.fixture
def frozen(monkeypatch) -> FrozenClock:
    fc = FrozenClock(DEFAULT_NOW)
    monkeypatch.setattr(clock, "utcnow", fc)
    return fc


@pytest.fixture
def db():
    session = SessionLocal()
    try:
        yield session
    finally:
        session.close()


@pytest.fixture
def client(frozen) -> TestClient:
    with TestClient(app, base_url="http://testserver") as c:
        yield c


class Factory:
    def __init__(self, db):
        self.db = db
        self._n = 0

    def department(self, name: str) -> Department:
        d = Department(name=name)
        self.db.add(d)
        self.db.commit()
        return d

    def user(self, name: str, role: Role = Role.EMPLOYEE, *, manager: User | None = None,
             department: Department | None = None, joined: date = date(2025, 1, 1), active: bool = True) -> User:
        self._n += 1
        u = User(
            employee_code=f"E{self._n:03d}", full_name=name, email=f"{name.lower().replace(' ', '.')}@test.example",
            password_hash=_PW_HASH, role=role, manager_id=manager.id if manager else None,
            department_id=department.id if department else None, date_joined=joined, is_active=active,
        )
        self.db.add(u)
        self.db.commit()
        return u

    def default_schedule(self) -> WorkSchedule:
        s = WorkSchedule(name="General", start_time=time(9, 30), end_time=time(18, 30), required_minutes=480,
                         working_days=[1, 2, 3, 4, 5], effective_from=date(2024, 1, 1))
        self.db.add(s)
        self.db.add(AttendancePolicy(id=1))
        self.db.commit()
        return s


@pytest.fixture
def factory(db) -> Factory:
    return Factory(db)


@pytest.fixture
def org(factory):
    """admin; Priya (manager) -> Rahul, Sneha; Anil (manager) -> Arjun; Vikram -> Priya, Anil (nested)."""
    factory.default_schedule()
    ai = factory.department("AI Engineering")
    se = factory.department("Software Engineering")
    admin = factory.user("Admin User", Role.ADMIN)
    vikram = factory.user("Vikram Desai", Role.MANAGER)
    priya = factory.user("Priya Sharma", Role.MANAGER, manager=vikram, department=ai)
    anil = factory.user("Anil Kumar", Role.MANAGER, manager=vikram, department=se)
    rahul = factory.user("Rahul Reddy", manager=priya, department=ai)
    sneha = factory.user("Sneha Rao", manager=priya, department=ai)
    arjun = factory.user("Arjun Kumar", manager=anil, department=se)
    return type("Org", (), dict(admin=admin, vikram=vikram, priya=priya, anil=anil, rahul=rahul, sneha=sneha,
                                arjun=arjun, ai=ai, se=se))


def login(client: TestClient, user: User, password: str = PASSWORD) -> dict:
    """Signs in and returns headers carrying the CSRF token for state-changing requests."""
    r = client.post("/api/auth/login", json={"email": user.email, "password": password})
    assert r.status_code == 200, r.text
    return {"X-CSRF-Token": r.json()["csrf_token"]}
