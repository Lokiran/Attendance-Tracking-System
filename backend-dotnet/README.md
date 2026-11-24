# Office Attendance Tracker API (.NET)

The backend of the Office Attendance Tracker: an ASP.NET Core (.NET 10) API on PostgreSQL, used by the React frontend
in `../frontend` through its `/api` proxy (port 8001).

> **History:** this API replaced the original FastAPI (Python) backend, which was removed after the port. Every
> endpoint, rule, import, export and management command was ported and checked response by response against it.
> The Python code and the migration plan are in the git history (before the commits that removed `backend/` and
> `docs/`). The few behaviours kept on purpose for compatibility are listed under [Parity notes](#parity-notes).

## Requirements

* .NET SDK 10.0.404 or a later 10.0 feature band (pinned in `global.json`).
* PostgreSQL 16 or 17, or a SQLite database file for local development. The API never creates or migrates a database
  on its own.

## Solution

| Project | Contents |
|---|---|
| `src/ATS.Domain` | Entities, enums and the pure rule engines (no framework references) |
| `src/ATS.Application` | Services, DTOs, settings, JSON wire format |
| `src/ATS.Infrastructure` | Database target and access, file import / export, password hashing, Entra ID |
| `src/ATS.Api` | Controllers, middleware (request id, security headers, error envelope), `Program.cs` |
| `tests/ATS.Tests` | xUnit + FluentAssertions + `WebApplicationFactory` |
| `config/wfo_mapping.json` | Accepted column names and work-mode / badge words for timesheet imports |

## Configuration

The settings (`DATABASE_URL`, `SECRET_KEY`, `OFFICE_TIMEZONE`, ...) are listed in `.env.example`. They are read from one
env file plus environment variables, and environment variables win:

1. `ATS_ENV_FILE`, if set (absolute, or relative to `backend-dotnet/`). The file must exist.
2. Otherwise `backend-dotnet/.env`, if it exists (it is gitignored).
3. Otherwise environment variables only.

Relative paths in the settings (such as `sqlite:///data/ats-dev.sqlite3` or `REPORT_LOGO_FILE=data/branding/logo.png`)
resolve against the env file's directory, so `backend-dotnet/data/` (gitignored) is the place for a local SQLite file,
backups and the report logo. There is no default database. The API refuses to start on a configuration problem:
production secrets and cookies, Entra ID settings, sign-in methods, or a missing SQLite file.

Profiles: `.env.example` (PostgreSQL) and `.env.sqlite.example` (local SQLite, selected with
`$env:ATS_ENV_FILE = ".env.sqlite"`).

## Run

```powershell
cd backend-dotnet
dotnet run --project src\ATS.Api            # http://127.0.0.1:8001, settings from backend-dotnet\.env
```

* Health: `GET http://127.0.0.1:8001/api/health`, `GET http://127.0.0.1:8001/api/health/db`
* API docs: `http://127.0.0.1:8001/api/docs` (Swagger UI), `/api/redoc`, `/api/openapi.json`
* The root URL (`http://127.0.0.1:8001/`) answers 404: the app itself is the frontend at `http://localhost:5173`.

## Container

```powershell
docker build -t ats-api backend-dotnet                                    # from the repository root
docker run --rm -p 127.0.0.1:8001:8001 --env-file <env file> ats-api       # the API on port 8001
docker run --rm --env-file <env file> ats-api schema-check                  # any command below, same image
```

The image (`Dockerfile`) builds with the SDK pinned in `global.json` and runs on the Ubuntu ASP.NET Core runtime,
which includes ICU and tzdata, as the non-root `app` user. It listens on port 8001 on all interfaces in the container
(`URLS`) and finds its files under `ATS_HOME=/app`, so an env file can be mounted at `/app/.env`. Nothing secret is in
the image, and `.dockerignore` keeps env files and databases out of the build context. CI builds the image, runs
`migrate` and `schema-check` against PostgreSQL 16 with `APP_ENV=production`, starts it and checks `/api/health/db`.
To reach PostgreSQL on the host from the container, use `host.docker.internal` (Docker Desktop) or `--network host`
(Linux) in `DATABASE_URL`.

## Database

EF Core migrations manage the schema. The baseline migration runs the original schema's exact DDL
(`src/ATS.Infrastructure/Persistence/Migrations/*/Baseline.*.sql`, the former Alembic head **0006**, including its
`alembic_version` table). The API never creates or migrates a database on its own; use the commands:

```powershell
cd backend-dotnet
dotnet run --project src\ATS.Api -- schema-check          # compare with the expected schema (read-only)
dotnet run --project src\ATS.Api -- migrate               # new, empty database (or apply pending EF migrations)
dotnet run --project src\ATS.Api -- ef-baseline           # adopt a database created by the former Python backend
dotnet run --project src\ATS.Api -- schema-catalog out.json  # write the schema catalog (read-only)
```

* **`migrate`** creates the schema in an empty database (a SQLite file is created if missing, never its folder) and
  applies pending EF migrations. It refuses a database created by Alembic (the former Python backend), or any
  non-empty database without EF history.
* **`ef-baseline`** adopts a database created by the former Python backend. It changes **nothing** unless
  `alembic_version` is `0006` and the schema matches the expected catalog exactly. Then it only adds EF's
  `__EFMigrationsHistory` table and records the baseline; all data stays. Take a backup first (`pg_dump`). Running it
  again is harmless. Such a database works with the API without it; it is needed before a future EF migration can be
  applied with `migrate`.
* **Expected schema:** `Persistence/Schema/expected-*.json` describes the baseline schema on PostgreSQL and SQLite.
* **Fixture:** `tests/ATS.Tests/Fixtures/python-0006.sqlite3` holds fictional rows written by the former Python models,
  so the tests prove the API reads the stored formats of existing databases.

## Reports

CSV reports, one employee's attendance as xlsx or PDF, and the WFO workbook, under `/api/reports/*` and
`/api/org/wfo/export.xlsx`. The PDF and the xlsx use `REPORT_COMPANY_NAME`, `REPORT_LOGO_FILE` (PNG or JPEG, for the
coloured header band, so a light logo), `REPORT_BRAND_COLOR` and `REPORT_ACCENT_COLOR`; without them they use the app's
navy and print no logo. The PDF uses the standard PDF fonts (no font files needed).

## Management commands

Run with the API's configuration (no web server):

```powershell
cd backend-dotnet
dotnet run --project src\ATS.Api -- seed [--reset-demo] [--reset-passwords]   # fictional demo data (refused in production)
dotnet run --project src\ATS.Api -- create-admin --email admin@company.com --name "Jane Doe" --code ADM100
dotnet run --project src\ATS.Api -- wfo-refresh --from 2026-10-01 --to 2026-10-31
```

* `seed` needs `SEED_ADMIN_EMAIL`, `SEED_ADMIN_PASSWORD` and `SEED_DEMO_PASSWORD` (environment or env file).
* `create-admin` asks for the password twice, or takes it from `ATS_ADMIN_PASSWORD`.
* `wfo-refresh` recomputes the stored work modes after schedule or holiday changes.

## Sign-in

* **Session cookie:** `ats_session` is an HttpOnly cookie; the database stores only its HMAC-SHA256 (keyed with
  `SECRET_KEY`). `ats_csrf` is readable by the SPA, which echoes it in `X-CSRF-Token` on every POST, PUT, PATCH and
  DELETE (double submit).
* **Expiry and throttling:** idle and absolute expiry follow `SESSION_IDLE_MINUTES` / `SESSION_ABSOLUTE_HOURS`, and
  failed logins are throttled per email and per IP (`LOGIN_*`).
* **Passwords:** Argon2id (argon2-cffi compatible encoding, so the hashes stored by the former backend verify unchanged).
* **Microsoft Entra ID:** authorization code with PKCE (`ENTRA_*`). The dev login (`DEV_LOGIN_ENABLED`) is refused in
  production.
* **Authorisation:** endpoints use `[Authorize(Policy = SessionAuth.User | ManagerOrAdmin | Admin)]`. The order of
  checks: session (401), CSRF (403 `csrf_failed`), then role (403 `forbidden`). `ScopeService` holds the
  reporting-line rules: a manager sees their whole subtree.

## Timesheet uploads

`POST /api/admin/attendance-uploads` takes a CSV / TSV / TXT, Excel (.xlsx, .xlsm, .xls) or PDF file of at most 5 MB
and returns a preview; nothing changes until `POST .../{id}/commit`. Column names and values (work modes, badge
sources) are matched through `config/wfo_mapping.json` (bundled; set `WFO_MAPPING_FILE` to use another file, absolute
or relative to the env file's folder). Unknown employee IDs can be added with `POST .../{id}/create-employees`; they get
a placeholder `@imported.example.com` address and no usable password.

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
* `ATS_TEST_POSTGRES_CONTAINER=1`: without a URL, start a throwaway `postgres:16` container with Testcontainers for the
  PostgreSQL tests (Docker must be running).
* `ATS_TEST_READONLY_DB_URL`: an existing database to read completely through the EF model. Nothing is written.

The rule-engine and file-reader tests (`RuleParityTests`, `UploadParityTests`) check fixtures that were generated by
the former Python code (`tests/ATS.Tests/Fixtures`); [`tests/COVERAGE.md`](tests/COVERAGE.md) maps the former pytest
suite to these tests.

**Deployment check:** `tools/cutover_check.py` (standard library Python) signs in with a real account (the password is
asked for, never shown), reads the main endpoints for that role and signs out; `--compare <url>` also diffs every read
against a second deployment on the same database.

## Parity notes

Behaviours kept from the original Python backend on purpose, so existing clients and stored data see no change. The
code marks each with `TODO(parity) P<n>` (or `parity note P<n>`).

| # | Behaviour |
|---|---|
| P1 | `/api/dashboard/employees?sort=check_in` is accepted but sorts by name (the service has no `check_in` key). |
| P2 | Two holidays on one date (organisation-wide and department): the attendance rules take the first name, the WFO calendar the last. |
| P3 | `GET /api/policy` creates the singleton policy row when it is missing, and saves it. |
| P4 | Trend buckets clip `end` to the range but not `start`, so a week bucket can start before the requested start date. |
| P5 | Row error messages quote values the Python way (`unknown employee '10001' (not in the app yet)`); they are stored and shown in the UI. |
| P6 | Employee endpoints answer 404 for people out of scope; WFO endpoints answer 403. |
| P7 | `/api/departments` counts members with one grouped query (Python counted per department); the output is identical. |
| P8 | Sorting and lower-casing use ordinal, culture-independent rules (`ToLowerInvariant`, `StringComparer.Ordinal`, stable sorts). |
| P9 | Month and week labels use the invariant culture (English month names on every host). |
| P10 | The employee trends response always has `headcount: 1`. |
| P11 | Storage only: a missing audit before/after value is written as SQL NULL; the JSON text `'null'` written by Python reads as null too. |
| P12 | Where Python answered 500, the API is kinder: an `inf` hours cell, a lone carriage return inside a CSV record, a broken workbook or a chart sheet, and a schedule PATCH sending a required field as null give a row problem or 422. |
| P13 | A request with an invalid path **and** an invalid body reports the body problems only (FastAPI listed both). |
| P14 | `PATCH /api/admin/devices/{id}` answers with the owner the device had **before** the change; the database and the next read have the new owner. |
