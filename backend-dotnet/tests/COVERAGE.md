# pytest → xUnit coverage map

The Python backend has 253 pytest functions in 21 files (`backend/app/tests`). This map shows where each file's cases
live in `tests/ATS.Tests`, and which cases have no .NET equivalent and why. Test names follow the Python names
(`test_login_failure_is_generic` → `Login_failure_is_generic`); several Python cases are sometimes one xUnit theory.

Besides these ports, the .NET suite has its own tests for things only the port needs (Python wire formats, the
Python-written SQLite fixture, the rule-engine and file-reader fixtures generated from the Python code, the EF baseline
command). Endpoint-level parity is checked outside xUnit by the scripts in `tools/parity` (see the README).

| Python file | Tests | .NET tests | Not ported |
|---|---:|---|---|
| `test_attendance_rules.py` | 18 | `Rules/AttendanceRulesTests` (+ `Rules/RuleParityTests` on generated fixtures) | — |
| `test_wfo_rules.py` | 28 | `Rules/WfoRulesTests` (+ `RuleParityTests`) | — |
| `test_attendance_api.py` | 10 | `Attendance/AttendanceApiTests` | — |
| `test_permissions.py` | 10 | `Attendance/DashboardApiTests` (the role and reporting-line cases) | — |
| `test_dashboards_reports.py` | 11 | `Attendance/DashboardApiTests` (dashboards, trends), `Reports/ReportTests` (CSV reports, `safe_cell`), `Admin/SettingsAndSeedTests` (seed) | — |
| `test_attendance_export.py` | 6 | `Reports/ReportTests` | — |
| `test_wfo_api.py` | 9 | `Attendance/DashboardApiTests` (reads, month check), `Uploads/WfoWriteTests` (override, leave, policy), `Reports/ReportTests` (WFO xlsx) | — |
| `test_wfo_upload.py` | 21 | `Uploads/UploadApiTests` | — |
| `test_timesheet_files.py` | 7 | `Uploads/UploadApiTests` (PDF import), `Uploads/UploadParityTests` (PDF table rebuild, glued dates / headers / labels, borderless tables, no table or text, text formats and unsupported files: all are cases in `tools/make_upload_fixtures.py`, read by both backends) | — |
| `test_scale.py` | 11 | `Attendance/ScaleTests` | — |
| `test_admin.py` | 15 | `Admin/AdminApiTests`, `Admin/AdminPostgresTests` | — |
| `test_devices.py` | 7 | `Admin/DeviceTests` | — |
| `test_auto_settings.py` | 6 | `Admin/SettingsAndSeedTests` | — |
| `test_auth.py` | 19 | `Auth/AuthApiTests` (login, rate limit, sessions, CSRF, change password), `Api/PipelineTests` (health, unknown route, security headers, CORS), `Unit/SettingsLoaderTests` (production secret) | — |
| `test_auth_entra.py` | 19 | `Auth/EntraTests` (sign-in flow, tokens, state, `next`, providers, dev login), `Auth/AuthApiTests` (password login off, login method audited), `Unit/SettingsLoaderTests` (partial Entra configuration, at least one method, dev login and https redirect in production) | — |
| `test_database_selection.py` | 12 | `Unit/SettingsLoaderTests`, `Unit/ConfigurationFilesTests` (URL required, PostgreSQL kept, relative paths, env file selection, the logged description hides the password), `Persistence/DatabaseStartupTests` (missing / uninitialised / initialised SQLite file, PostgreSQL not probed, `.env.example`) | — |
| `test_migrations.py` | 20 | `Persistence/SchemaTests` (migrated schema equals the Alembic head schema; baseline adoption), `Unit/ConfigurationFilesTests` (URL normalisation) | Alembic-specific: upgrade / downgrade / re-upgrade, offline SQL, the psycopg3 driver URL, and the per-revision 0002–0006 steps. .NET has one baseline migration equal to Alembic head and adopts existing databases with `ef-baseline`; the 0005 / 0006 data moves already ran in every Alembic database |
| `postgres/test_pg_migrations.py` | 11 | `Persistence/PostgresBehaviourTests` (btree_gist, exclusion constraint: overlap, adjacent, voided, per employee; one open session; check constraints; settings defaults and checks), `Persistence/SchemaTests` (PostgreSQL schema) | Downgrade / re-upgrade (Alembic-specific) |
| `postgres/test_pg_scope.py` | 4 | `Persistence/PostgresBehaviourTests` (recursive subtree, cycle, API scope including the day detail), `Auth/ScopeTests` (SQLite) | — |
| `postgres/test_pg_transactions.py` | 3 | `Persistence/PostgresBehaviourTests` (UTC round trip under any session time zone, failed commit rolls back) | `test_driver_is_psycopg3` (Python driver) |
| `test_identities.py` | 6 | — | All: `services/identities.py` serves only the standalone `scripts/backfill_identities.py`, which is not part of the API and is not ported |

## Running the PostgreSQL tests

The tests marked `[PostgresFact]` need PostgreSQL and are skipped without it:

* `ATS_TEST_POSTGRES_URL=postgresql://user:password@host:5432/ats_test`: a disposable database whose name contains
  `test`. **Every table in it is dropped.**
* or `ATS_TEST_POSTGRES_CONTAINER=1`: Testcontainers starts a throwaway `postgres:16` container (Docker must be running).
* `ATS_REQUIRE_POSTGRES=1` turns the skip into a failure (CI sets it).
