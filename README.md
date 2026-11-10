# Office Attendance Tracker (ATS)

A full-stack attendance system for **Nexa Technologies** (fictional demo company). Employees
check in and out, and managers monitor their reporting line. Administrators get
organisation-wide analytics, management screens and an audit trail. Every number on every
dashboard is computed by the backend from database records.

| | |
|---|---|
| Frontend | React 19, Vite 8, TypeScript, Tailwind CSS 4, React Router 7, TanStack Query 5, React Hook Form + Zod, Recharts 3, Lucide |
| Backend | Python 3.11+ (tested on 3.14), FastAPI, Uvicorn, SQLAlchemy 2, Alembic, Pydantic 2, psycopg 3, Argon2id |
| Database | PostgreSQL 15+ (16 recommended) |
| Tests | Pytest (backend), Vitest + Testing Library (frontend), `scripts/smoke_test.py` (end-to-end over HTTP) |

More detail:
* [docs/ATTENDANCE_RULES.md](docs/ATTENDANCE_RULES.md): exact calculation rules.
* [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): structure, security model, full API list.

## Features

* **Employee dashboard**
  * Check-in/check-out using server time, with the elapsed time shown live.
  * Summary cards: present, absent, shift hours, hours worked, average per day, attendance %.
  * Monthly calendar with per-day session details.
  * Charts: daily net hours, 6-month trend, status distribution, actual vs required hours.
  * Filterable, paginated history.
  * Correction requests with status tracking, plus holidays. There is no leave: attendance is office
    presence, measured against the shift's hours.
* **Manager dashboard** (whole reporting subtree)
  * Today's snapshot cards: categories are mutually exclusive and clickable as filters.
  * Daily stacked attendance and weekly/monthly trends.
  * Employee-wise hours and attendance comparisons.
  * Team table with search, status filter, sorting and pagination.
  * Exceptions list including missing check-outs.
  * Drill-down to any employee and day.
  * Approvals and CSV exports.
* **Admin**
  * Organisation overview with department, manager, distribution and office-hours comparisons.
  * Searchable, expandable hierarchy (organisation → department → manager → employee),
    with department and manager dashboards.
  * Employee, department, schedule, holiday and policy management with server-side safeguards.
  * Searchable audit log and all reports.
* **Security**
  * HttpOnly session cookies and CSRF tokens.
  * Argon2id passwords and login throttling.
  * Role and reporting-scope authorization on every endpoint and export.
  * Secure-by-default production config and audited changes.
* Responsive layout, keyboard-accessible controls, light/dark/system theme, and a table view for
  every chart.

## Repository layout

```
ATS/
├── backend/            FastAPI app (app/), Alembic migrations, tests, requirements.txt,
│                       .env.example (PostgreSQL), .env.sqlite.example (local SQLite),
│                       data/ (local SQLite database files, git-ignored)
├── frontend/           React app (src/), Vite config, tests, .env.example
├── database/           PostgreSQL notes + Docker init script (creates ats_test)
├── docs/               Attendance rules, architecture/security/API
├── scripts/            setup.ps1 (prerequisites + install), run-local-sqlite.ps1 (SQLite profile),
│                       test-postgres.ps1 (PostgreSQL verification), smoke_test.py (end-to-end checks)
├── docker-compose.yml  Optional PostgreSQL
├── .env.example        Variables for docker compose
└── README.md
```

## Choosing a database

The database is always selected **explicitly**:
* There is no default database. The backend refuses to start without `DATABASE_URL`.
* It never falls back from PostgreSQL to SQLite, or the reverse.
* It never creates a database implicitly.

| | Local development: SQLite | Deployment and verification: PostgreSQL |
|---|---|---|
| Env file | `backend\.env.sqlite` (template `backend\.env.sqlite.example`), selected with `ATS_ENV_FILE=.env.sqlite` | `backend\.env` (template `backend\.env.example`), loaded by default |
| Database | `backend\data\ats-dev.sqlite3` | PostgreSQL 15+ (Docker Compose or a server) |
| Start | `scripts\run-local-sqlite.ps1` | `uvicorn …` with `backend\.env` |
| Overlap protection | service-level checks | service-level checks **plus** the `btree_gist` exclusion constraint |

* The backend logs the selected database and env file on startup, e.g.
  `database: SQLite file C:\Dev\ATS\backend\data\ats-dev.sqlite3 (env file: …\.env.sqlite)`.
* An environment variable `DATABASE_URL` overrides any env file. Unset it when using a profile.
  The SQLite launcher refuses to start if it is set.

Prerequisites:
* Python 3.11+
* Node.js 20.19+ (LTS)
* PostgreSQL 15+ (or Docker Desktop), for the PostgreSQL path only

Install dependencies once (this also writes `backend\.env` and `frontend\.env` if they are missing;
existing `.env` files are never overwritten; `-CheckOnly` only checks prerequisites):

```powershell
cd C:\Dev\ATS
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
```

## Option 1 — Local development with SQLite

No PostgreSQL needed. Two terminals.

### One-time profile setup

```powershell
cd C:\Dev\ATS\backend
copy .env.sqlite.example .env.sqlite
# Edit .env.sqlite: set SECRET_KEY (python -c "import secrets; print(secrets.token_urlsafe(48))")
# and SEED_ADMIN_PASSWORD / SEED_DEMO_PASSWORD if you want demo data.
```

`DATABASE_URL=sqlite:///data/ats-dev.sqlite3` is resolved against `backend\`, whatever your
current directory is.

### Terminal 1 — Backend (http://127.0.0.1:8000)

```powershell
cd C:\Dev\ATS
powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1               # existing database
powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1 -Init         # create/upgrade schema first
powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1 -Init -Seed   # ... and add demo data
```

The launcher:
1. Selects `backend\.env.sqlite` via `ATS_ENV_FILE`.
2. Refuses if `DATABASE_URL` is set in the shell, or if the port is busy (`-Port 8001` to change it). It never stops other processes.
3. Only touches the schema with `-Init` (`alembic upgrade head`, which only creates the file or applies pending migrations).

Without the script:

```powershell
cd C:\Dev\ATS\backend
$env:ATS_ENV_FILE = ".env.sqlite"
.\.venv\Scripts\alembic.exe upgrade head        # first time only (creates data\ats-dev.sqlite3)
.\.venv\Scripts\python.exe -m app.cli seed      # optional demo data
.\.venv\Scripts\python.exe -m uvicorn app.main:app --reload --port 8000
```

If the SQLite file is missing or has no schema, the backend stops with instructions instead of
silently creating an empty database.

### Terminal 2 — Frontend (http://localhost:5173)

```powershell
cd C:\Dev\ATS\frontend
npm run dev
```

## Option 2 — PostgreSQL (deployment and verification)

Configure `backend\.env` from `backend\.env.example` with a PostgreSQL `DATABASE_URL`:
`setup.ps1 -DbPassword "<pw>"` fills it in. Don't set `ATS_ENV_FILE`.

### Terminal 1 — Database

**Option A: Docker** (uses the root `.env`, created by `setup.ps1 -DbPassword "<pw>"`):

```powershell
cd C:\Dev\ATS
copy .env.example .env      # only if setup.ps1 did not create it; then set POSTGRES_PASSWORD
docker compose up -d db
docker compose ps           # wait for "healthy"
```

**Option B: local PostgreSQL.** In `psql` as a superuser:

```sql
CREATE ROLE ats LOGIN PASSWORD 'choose-a-db-password';
CREATE DATABASE ats OWNER ats;
CREATE DATABASE ats_test OWNER ats;   -- for the PostgreSQL integration tests
```

### Terminal 2 — Backend (http://127.0.0.1:8000)

```powershell
cd C:\Dev\ATS\backend
Remove-Item Env:ATS_ENV_FILE -ErrorAction SilentlyContinue   # use backend\.env
.\.venv\Scripts\alembic.exe upgrade head
.\.venv\Scripts\python.exe -m app.cli seed               # optional: demo data (development only)
.\.venv\Scripts\python.exe -m uvicorn app.main:app --reload --host 127.0.0.1 --port 8000
```

* API docs: http://127.0.0.1:8000/api/docs
* Health checks: http://127.0.0.1:8000/api/health and http://127.0.0.1:8000/api/health/db
  (the latter returns `{"database": "sqlite"}` or `{"database": "postgresql"}`)

### Terminal 3 — Frontend

As in Option 1: `cd C:\Dev\ATS\frontend; npm run dev`, then open http://localhost:5173.

### Verifying PostgreSQL

Run the full backend suite, including the PostgreSQL integration tests, against a **disposable**
database whose name contains `test` (see [Tests](#tests)):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test-postgres.ps1 -UseDocker
powershell -ExecutionPolicy Bypass -File scripts\test-postgres.ps1 -DatabaseUrl "postgresql+psycopg://ats:<pw>@<host>:5432/ats_test"
```

## Demo users

`python -m app.cli seed` creates the users below. It also creates 75 days of attendance (including
multi-session days, missing check-outs, overnight shifts, holidays and absences) plus
pending, approved and rejected corrections. All dates are relative to the current day.

| Name | Role | Email | Reports to |
|---|---|---|---|
| System Administrator | HR admin (`ADMIN`, not counted in statistics) | value of `SEED_ADMIN_EMAIL` | — |
| Vikram Desai | MANAGER, Operations (COO) | vikram.desai@nexa.example | — |
| Priya Sharma | MANAGER, AI Engineering | priya.sharma@nexa.example | Vikram Desai |
| Anil Kumar | MANAGER, Software Engineering | anil.kumar@nexa.example | Vikram Desai |
| Meera Nair | MANAGER, Human Resources | meera.nair@nexa.example | — |
| Rahul Reddy, Sneha Rao, Farah Khan, Neha Joshi | EMPLOYEE, AI Engineering | firstname.lastname@nexa.example | Priya Sharma |
| Arjun Kumar, Ananya Sharma, Rohan Gupta (night shift), Vivek Singh | EMPLOYEE, Software Engineering | … | Anil Kumar |
| Divya Menon | EMPLOYEE, Human Resources | divya.menon@nexa.example | Meera Nair |
| Kiran Patel | EMPLOYEE, Operations (Mon–Sat schedule) | kiran.patel@nexa.example | Vikram Desai |

That is 10 employees. Every tracked person also gets a demo device MAC (`users.wifi_identity`, in the
locally administered `02:a7:5e:…` range) and a demo Entra object id (`users.graph_user_id`) for the
automatic-attendance collectors. The seed fills these only where they are empty.

Passwords:
* The admin uses `SEED_ADMIN_PASSWORD`; everyone else uses `SEED_DEMO_PASSWORD`, both read from
  the selected env file (`backend\.env`, or `backend\.env.sqlite` with the SQLite profile).
* Nothing is hard-coded, and the seed refuses to run without them or in production.
* The seed is idempotent. Re-running never duplicates people or attendance.
* `python -m app.cli seed --reset-demo` regenerates demo attendance relative to today.
* `--reset-passwords` re-applies the passwords from `.env`.

**Production / real deployments**: don't seed. Create the first administrator interactively;
the password is prompted, not passed on the command line:

```powershell
python -m app.cli create-admin --email admin@yourcompany.com --name "Jane Doe" --code ADM001
```

## WFO (work from office) tracking

Every employee-day gets a work mode: Office, WFH, Leave, Holiday or Weekly off. Monthly WFO days, WFO %,
weekly progress ("2 of 3 days") and policy compliance are computed from those days.

* **Signals**, highest priority first: an Admin override; the work-mode column of an imported file; badge /
  swipe rows; office Wi-Fi; Office / WFH chosen at check-in. Worked hours with no office signal count as WFH.
* **Pages:** *Office days* (everyone), *Team office days* (managers), *Org office days* and *Import attendance*
  (admin, with Excel export and day overrides).
* **Imports:** CSV or Excel with a preview first; examples are in `samples/`. Accepted header names and values
  are in `backend/config/wfo_mapping.json`.
* **Policy:** 3 office days a week, 12 a month, at least 4 h in the office for a day to count (editable).

Rules, API, upload format and assumptions: [docs/wfo.md](docs/wfo.md). After changing schedules or
holidays, recompute stored work modes with:

```powershell
python -m app.cli wfo-refresh --from 2026-10-01 --to 2026-10-31
```

## Signing in

Three methods, each switched on by configuration. The login page shows only the enabled ones
(`GET /api/auth/providers`).

| Method | Turn on with | Use |
|---|---|---|
| **Microsoft Entra ID** | `ENTRA_*` variables | The target for everyone. People must already exist in the app (HR adds them); the first Microsoft sign-in links their Entra object id, after that they are matched by it. Roles stay managed in the app |
| Email + password | `PASSWORD_LOGIN_ENABLED=true` (default) | Until the Entra app registration is live |
| Dev bypass | `DEV_LOGIN_ENABLED=true` | Local testing: pick any active user by email, no password. Audited as method `dev`; impossible in production |

Entra app registration (done by IT): platform **Web**, redirect URI
`https://<your host>/api/auth/entra/callback` (development through the Vite proxy:
`http://localhost:5173/api/auth/entra/callback`), a client secret, and the delegated scopes
`openid profile email`. The app checks the ID token's signature against the tenant's published keys,
plus issuer, audience, expiry, nonce and tenant. It uses PKCE and a short-lived signed cookie for
`state` and `nonce`.

Try the dev bypass locally:

```powershell
$env:DEV_LOGIN_ENABLED = "true"
powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1
```

## Environment variables

The backend reads one env file: `backend\.env` by default, or the file named by `ATS_ENV_FILE`
(absolute, or relative to `backend\`; it must exist). Real environment variables take precedence
over the file. Templates: `backend\.env.example` (PostgreSQL) and `backend\.env.sqlite.example`
(local SQLite).

| Variable | Default | Notes |
|---|---|---|
| `ATS_ENV_FILE` | `backend\.env` | Set as a real environment variable to select a profile, e.g. `.env.sqlite` |
| `APP_ENV` | `development` | `production` enables strict checks (strong secret, secure cookies, no wildcard CORS) |
| `DATABASE_URL` | **required, no default** | PostgreSQL: `postgresql+psycopg://ats:<password>@localhost:5432/ats`. SQLite: `sqlite:///data/ats-dev.sqlite3` (relative to `backend\`) |
| `SECRET_KEY` | — | ≥ 32 random chars; HMAC key for session tokens. If empty in development, an ephemeral key is used (sessions reset on restart) |
| `OFFICE_TIMEZONE` | `Asia/Kolkata` | IANA zone for workday boundaries and display |
| `SESSION_IDLE_MINUTES` / `SESSION_ABSOLUTE_HOURS` | 120 / 12 | Session expiry |
| `COOKIE_SECURE` / `COOKIE_SAMESITE` / `COOKIE_DOMAIN` | auto / `lax` / — | `COOKIE_SECURE` is true in production |
| `CORS_ORIGINS` | `http://localhost:5173` | Comma-separated |
| `LOGIN_MAX_FAILURES` / `LOGIN_WINDOW_MINUTES` | 5 / 15 | Brute-force throttling |
| `TRUST_PROXY_HEADERS` | `false` | Only behind a trusted reverse proxy |
| `WFO_MAPPING_FILE` | `config\wfo_mapping.json` | Accepted header names and work-mode / badge values for WFO imports (relative to `backend\`) |
| `PASSWORD_LOGIN_ENABLED` | `true` | Email/password sign-in. Turn off once Microsoft sign-in is live |
| `ENTRA_TENANT_ID` / `ENTRA_CLIENT_ID` / `ENTRA_CLIENT_SECRET` / `ENTRA_REDIRECT_URI` | — | Microsoft Entra ID sign-in; set all four or none. See [Signing in](#signing-in) |
| `ENTRA_AUTHORITY` | `https://login.microsoftonline.com` | Only for sovereign clouds |
| `DEV_LOGIN_ENABLED` | `false` | Local testing only: sign in as any active user without a password. Refused when `APP_ENV=production` |
| `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` / `SEED_DEMO_PASSWORD` | — | Demo seed only |
| `TEST_DATABASE_URL` | — | Optional PostgreSQL database for the test suite (it is wiped; name must contain `test`) |

`postgresql://` and `postgres://` URLs are rewritten to `postgresql+psycopg://`, because only the
psycopg 3 driver is installed. PostgreSQL connections time out after 10 seconds; the API then
answers 503 instead of hanging.

`frontend\.env`:

| Variable | Default |
|---|---|
| `VITE_API_BASE_URL` | `/api` |
| `VITE_DEV_BACKEND_URL` | `http://127.0.0.1:8000` |
| `VITE_DASHBOARD_REFRESH_MS` | `30000` (`0` disables polling) |

Root `.env` is used by Docker Compose only: `POSTGRES_USER`, `POSTGRES_PASSWORD`,
`POSTGRES_DB`, `POSTGRES_PORT`.

## Tests

The backend suite has two explicitly separated groups:

| Group | Location / marker | Runs on |
|---|---|---|
| Portable tests | everything outside `app/tests/postgres/` | SQLite (default, temporary file) or PostgreSQL |
| PostgreSQL integration tests | `app/tests/postgres/`, marker `postgres` | PostgreSQL only; **reported as SKIPPED** without `TEST_DATABASE_URL` |

The PostgreSQL integration tests cover:
* Alembic upgrade, downgrade and re-upgrade, including `btree_gist`, the exclusion constraint, the partial unique index and `timestamptz` columns.
* Overlap, unique and check constraint SQLSTATEs.
* Row-lock waits, parallel check-ins and check-outs, and the unique index acting as the last line of defence.
* Transaction rollback, timezone-independent timestamps, the recursive-CTE reporting scope, and API scope checks.

```powershell
# Backend, SQLite (PostgreSQL integration tests are skipped and listed with -rs)
cd C:\Dev\ATS\backend
.\.venv\Scripts\python.exe -m pytest -rs
.\.venv\Scripts\python.exe -m pytest -m "not postgres"     # portable tests only

# Backend against PostgreSQL: everything, with missing PostgreSQL treated as an error.
# The database is WIPED and its name must contain "test".
cd C:\Dev\ATS
powershell -ExecutionPolicy Bypass -File scripts\test-postgres.ps1 -UseDocker
powershell -ExecutionPolicy Bypass -File scripts\test-postgres.ps1 -DatabaseUrl "postgresql+psycopg://ats:<password>@<host>:5432/ats_test"
# (equivalent: $env:TEST_DATABASE_URL = "..."; backend\.venv\Scripts\python.exe -m pytest --require-postgres -rs)

# Frontend
cd C:\Dev\ATS\frontend
npm test
npm run typecheck
npm run build

# End-to-end smoke test against the running stack with seeded data
cd C:\Dev\ATS
$env:SEED_ADMIN_PASSWORD = "..."; $env:SEED_DEMO_PASSWORD = "..."
backend\.venv\Scripts\python.exe scripts\smoke_test.py
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Port 5173 is already in use` | Vite uses a strict port. Stop the other process, or set `VITE_PORT=5174` in `frontend\.env` and add that origin to `CORS_ORIGINS`. |
| Port 8000 busy | Run Uvicorn with `--port 8001` and set `VITE_DEV_BACKEND_URL=http://127.0.0.1:8001`. |
| Find the process holding a port | `Get-NetTCPConnection -LocalPort 8000 -State Listen \| Select OwningProcess` |
| "Unable to reach the attendance service" in the browser | The backend isn't running, or the proxy target is wrong. Check `http://127.0.0.1:8000/api/health`. |
| `DATABASE_URL is not set and there is no default database` | No env file was found and no variable is set. Use `scripts\run-local-sqlite.ps1` (SQLite), or create `backend\.env` (PostgreSQL). |
| `SQLite database file not found` / `has no application schema` | The SQLite profile points at a missing or empty file. Run `scripts\run-local-sqlite.ps1 -Init`. Nothing is created automatically. |
| `/api/health/db` returns 503 | The configured PostgreSQL is down, or `DATABASE_URL` is wrong. There is deliberately no SQLite fallback; check `docker compose ps` or your PostgreSQL service, or use the SQLite profile. |
| Backend uses PostgreSQL although you wanted SQLite | A `DATABASE_URL` environment variable overrides the env file (`Remove-Item Env:DATABASE_URL`), or `ATS_ENV_FILE` isn't set. Check the `database:` line in the startup log. |
| `password authentication failed` | The password in `DATABASE_URL` doesn't match the role. With Docker, the password is fixed when the volume is first created; run `docker compose down -v` to recreate it (this deletes data). |
| `permission denied to create extension "btree_gist"` | Use PostgreSQL 13+, where it is a trusted extension, or run `CREATE EXTENSION btree_gist;` once as a superuser in the `ats` database. |
| `Activate.ps1 cannot be loaded because running scripts is disabled` | `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`, or call `.\.venv\Scripts\python.exe` directly. |
| Docker Desktop fails to start | Docker Desktop needs WSL 2 (`wsl --install`) or Hyper-V. Otherwise install PostgreSQL natively (Option B). |
| Signed out unexpectedly | Sessions expire after 2 h idle or 12 h total. In development without `SECRET_KEY`, every backend restart also signs everyone out. |
| HTTP 429 on login | Too many failed attempts; wait 15 minutes (`LOGIN_WINDOW_MINUTES`). |
| Dashboards empty | No data in the period, or no schedule covers those days. Seed demo data, or add an organisation default schedule under Admin → Schedules. |

## Known limitations

* Manual check-in records events submitted from the browser; it doesn't prove physical
  presence (no badge, geofence or network checks).
* Sessions that start after midnight count toward the new day, even when they continue a night
  shift after a break (see the attendance rules doc).
* Breaks are recorded per session (entered at check-out or via correction). There is no
  automatic break deduction policy.
* Login throttling is per email/IP in the database. There is no CAPTCHA or account-lockout
  notification.
* Dashboards compute statistics in the API process. That is fine for hundreds of employees;
  thousands would need pre-aggregated daily tables or caching.
* Notifications (email/Slack) and multi-location holiday calendars
  are out of scope for this version.
