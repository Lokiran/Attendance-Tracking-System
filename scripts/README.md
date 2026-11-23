# Scripts

Run everything from the repository root in PowerShell (`powershell -ExecutionPolicy Bypass -File scripts\<name>.ps1`).
The Python scripts need only Python 3.11+ (`smoke_test.py`: standard library; `make_wfo_samples.py`: `openpyxl`).

| Script | What it does | When to use it |
|---|---|---|
| `setup.ps1` | Checks prerequisites (.NET SDK, Node.js, PostgreSQL / Docker, ports), restores the API's packages, installs the frontend packages, creates `.env` files from the examples | Once, on a new machine |
| `run-local-sqlite.ps1` | Starts the API on the local SQLite profile (`backend-dotnet/.env.sqlite`); `-Init` creates the database, `-Seed` adds demo data | Day-to-day development without PostgreSQL |
| `test-postgres.ps1` | Runs the API tests against PostgreSQL, PostgreSQL tests required (the target database is wiped; its name must contain "test") | Before a release, once PostgreSQL exists |
| `smoke_test.py` | End-to-end checks over HTTP against the running app with the demo seed data (sign-in, dashboards, an import preview, exports) | After a change that touches several layers |
| `make_wfo_samples.py` | Regenerates the sample import files in `samples/` | When the import format changes |

The read-only check of a deployment on real data is `backend-dotnet/tools/cutover_check.py`.
