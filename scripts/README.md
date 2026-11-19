# Scripts

Run everything from the repository root in PowerShell. Python scripts use the backend's virtual environment:
`backend\.venv\Scripts\python.exe scripts\<name>.py`.

| Script | What it does | When to use it |
|---|---|---|
| `setup.ps1` | Checks prerequisites, creates the Python venv, installs backend and frontend packages, creates `.env` files from the examples | Once, on a new machine |
| `run-local-sqlite.ps1` | Starts the backend on the local SQLite profile (`backend/.env.sqlite`); `-Init` creates the database | Day-to-day development |
| `test-postgres.ps1` | Runs the backend tests against PostgreSQL (the target database is wiped; its name must contain "test") | Before a release, once PostgreSQL exists |
| `pg_probe.py` | Prints the PostgreSQL version and checks the `btree_gist` extension; called by `test-postgres.ps1` | Not run directly |
| `smoke_test.py` | End-to-end checks over HTTP against the running app with the demo seed data (sign-in, dashboards, an import preview, exports) | After a change that touches several layers |
| `scale_benchmark.py` | Times the dashboards on a throw-away database with thousands of generated employees | After a change to dashboard or attendance queries |
| `make_wfo_samples.py` | Regenerates the sample import files in `samples/` | When the import format changes |
| `copy_sqlite_to_postgres.py` | Copies every table from the SQLite file (`backend/.env.sqlite`) into an empty PostgreSQL database (`backend/.env`); check first, `--apply` to copy; one transaction, counts compared | Once, when moving local data to PostgreSQL |
| `backfill_identities.py` | Loads Microsoft / Wi-Fi identities from a CSV (dry run unless `--apply`) | Automatic attendance only (on hold, see `docs/plans/automatic-attendance.md`) |
