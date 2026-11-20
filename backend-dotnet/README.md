# Office Attendance Tracker API (.NET)

ASP.NET Core port of the FastAPI backend in `../backend`. The React frontend works unchanged; only its proxy target
moves from port 8000 to 8001 at cutover. Plan, decisions and progress: [`../docs/dotnet-migration-plan.md`](../docs/dotnet-migration-plan.md).

> **Status: phase 3 of 9**: scaffold, configuration, logging, OpenAPI, health and the request pipeline; domain model, EF Core
> mapping and migrations matching the Alembic schema; sign-in, sessions and role / reporting-line authorisation
> (`/api/auth/*`). The Python backend remains the working backend until cutover.

## Requirements

* .NET SDK 10.0.404 or a later 10.0 feature band (pinned in `global.json`).
* PostgreSQL 16 or 17, or an existing SQLite database file. The API never creates or migrates a database on its own.

## Solution

| Project | Contents |
|---|---|
| `src/ATS.Domain` | Entities, enums and the pure rule engines (no framework references) |
| `src/ATS.Application` | Services, DTOs, settings, JSON wire format, Python-compatibility helpers |
| `src/ATS.Infrastructure` | Database target and access, file import / export, password hashing, Entra ID |
| `src/ATS.Api` | Controllers, middleware (request id, security headers, error envelope), `Program.cs` |
| `tests/ATS.Tests` | xUnit + FluentAssertions + `WebApplicationFactory` |

## Configuration

The settings use **the same keys as the Python backend** (`DATABASE_URL`, `SECRET_KEY`, `OFFICE_TIMEZONE`, ...):
see `.env.example`. They are read from one env file plus environment variables, and environment variables win:

1. `ATS_ENV_FILE`, if set (absolute, or relative to `backend-dotnet/`). The file must exist.
2. Otherwise `backend-dotnet/.env`, if it exists (it is gitignored).
3. Otherwise environment variables only.

Relative paths in the settings (such as `sqlite:///data/ats-dev.sqlite3`) resolve against the env file's directory,
so reusing a Python env file reaches the same database. There is no default database. The startup refusals are the
Python ones: production secrets and cookies, Entra ID settings, sign-in methods, and a missing SQLite file.

## Run

```powershell
cd backend-dotnet
# Reuse the Python backend's PostgreSQL settings (or create backend-dotnet\.env from .env.example):
$env:ATS_ENV_FILE = "..\backend\.env"
dotnet run --project src\ATS.Api            # http://127.0.0.1:8001
```

* Health: `GET http://127.0.0.1:8001/api/health`, `GET http://127.0.0.1:8001/api/health/db`
* API docs: `http://127.0.0.1:8001/api/docs` (Swagger UI), `/api/redoc`, `/api/openapi.json`

## Database

The schema is the Python backend's, at Alembic head **0006**. EF Core migrations replace Alembic: the baseline
migration runs Alembic's exact DDL (`src/ATS.Infrastructure/Persistence/Migrations/*/Baseline.*.sql`), so a database
created by .NET is identical to one created by `alembic upgrade head`. That includes `alembic_version` = `0006`, so the
Python backend also accepts it until cutover. The API never creates or migrates a database on its own; use the commands:

```powershell
cd backend-dotnet
dotnet run --project src\ATS.Api -- schema-check          # compare with Alembic head 0006 (read-only)
dotnet run --project src\ATS.Api -- migrate               # new, empty database only
dotnet run --project src\ATS.Api -- ef-baseline           # adopt an existing Alembic database (see below)
dotnet run --project src\ATS.Api -- schema-catalog out.json  # write the schema catalog (read-only)
```

* **`migrate`** creates the schema in an empty database (a SQLite file is created if missing). It refuses a database
  that Alembic created, or any non-empty database without EF history.
* **`ef-baseline`** adopts an existing Python database. It changes **nothing** unless `alembic_version` is `0006` and
  the schema matches the expected catalog exactly. Then it only adds EF's `__EFMigrationsHistory` table and records
  the baseline; `alembic_version` and all data stay. Take a backup first (`pg_dump`). Running it again is harmless.
* **Expected schema:** `Persistence/Schema/expected-*.json` was read from new databases built by
  `alembic upgrade head`. Regenerate it only from such a database:
  `$env:ATS_CATALOG_SOURCE="alembic upgrade head (0006) on a new PostgreSQL database"; dotnet run --project src\ATS.Api -- schema-catalog <absolute path>`.
* **Python-written fixture:** `tests/ATS.Tests/Fixtures/python-0006.sqlite3` holds fictional rows written by the Python
  models, so the tests prove .NET reads Python's storage formats. Rebuild it with
  `backend\.venv\Scripts\python.exe backend-dotnet\tools\make_python_fixture.py`.

## Sign-in

Exactly the Python mechanism, so the frontend needs no change and both backends accept the same sessions when they
share a database and `SECRET_KEY`:

* **Session cookie:** `ats_session` is an HttpOnly cookie; the database stores only its HMAC-SHA256 (keyed with
  `SECRET_KEY`). `ats_csrf` is readable by the SPA, which echoes it in `X-CSRF-Token` on every POST, PUT, PATCH and
  DELETE (double submit).
* **Expiry and throttling:** idle and absolute expiry follow `SESSION_IDLE_MINUTES` / `SESSION_ABSOLUTE_HOURS`, and
  failed logins are throttled per email and per IP (`LOGIN_*`).
* **Passwords:** Argon2id, compatible with argon2-cffi. Existing hashes verify unchanged, and hashes written here
  verify in Python.
* **Microsoft Entra ID:** authorization code with PKCE (`ENTRA_*`). The dev login (`DEV_LOGIN_ENABLED`) is refused in
  production.
* **Authorisation:** endpoints use `[Authorize(Policy = SessionAuth.User | ManagerOrAdmin | Admin)]`. The order of
  checks is Python's: session (401), CSRF (403 `csrf_failed`), then role (403 `forbidden`). `ScopeService` holds the
  reporting-line rules: a manager sees their whole subtree.

## Build and test

```powershell
cd backend-dotnet
dotnet build ATS.sln          # warnings are errors
dotnet test ATS.sln
```

The tests never read a developer env file (`ATS_ENV_FILE` points at an empty file during the run) and use
temporary SQLite files. Optional:

* `ATS_TEST_POSTGRES_URL`: a disposable PostgreSQL database whose name contains `test`. **Every table in it is
  dropped.** It enables the PostgreSQL tests (`ATS_REQUIRE_POSTGRES=1` makes them fail instead of skip, for CI).
* `ATS_TEST_READONLY_DB_URL`: an existing database to read completely through the EF model. Nothing is written.
