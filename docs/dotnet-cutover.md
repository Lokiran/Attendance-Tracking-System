# Cutover: Python backend → .NET backend

> **Status (2026-11-23): the Python backend has been removed from the repository.** The .NET API runs on the
> existing database as it is, and the frontend proxies to it by default. Kept for reference: steps 3–5 (backup,
> `schema-check`, `ef-baseline`) are **still to be done** on the real database, with a go-ahead, before a future EF
> migration can be applied with `migrate`. The rollback section no longer applies (the Python code is only in the
> git history), and the settings now live in `backend-dotnet/.env`.

The .NET API (`backend-dotnet/`) replaces the FastAPI backend (`backend/`) on the **same PostgreSQL database**. The
frontend does not change: only the address its `/api` proxy points to moves from port 8000 to port 8001. The Python
backend keeps running, untouched, until the switch is confirmed, so rolling back means pointing the proxy back.

Do the steps in order. Steps 3 and 5 touch the production database, and each needs an explicit go-ahead.

## 1. Before the day

- [ ] Branch `feature/dotnet-backend` is reviewed and merged, with the pipeline green (`azure-pipelines.yml`; the
      GitHub workflow has since been removed). The jobs cover the .NET tests on PostgreSQL 16, the container image, the Python
      backend and the frontend.
- [ ] The Python backend runs the commit that makes Alembic ignore `__EFMigrationsHistory` (`backend/alembic/env.py`).
      Without it, `alembic check` reports that table as drift after step 5.
- [ ] Optional rehearsal on a **copy** of production (restore the step 3 dump into a scratch database). Run both
      backends on the copy and use `backend-dotnet/tools/parity/compare.py`, `compare_reports.py` and
      `tools/cutover_check.py --compare`.
- [ ] Choose a quiet window and tell users. No timesheet imports or administration changes during the window.

## 2. Configuration

The .NET API reads the same settings, with the same names, as the Python backend.

- [ ] Either point it at the existing file (`$env:ATS_ENV_FILE = "..\backend\.env"`, resolved from `backend-dotnet\`),
      or copy `backend\.env` to `backend-dotnet\.env`. With Docker, pass the values as environment variables or
      mount the file at `/app/.env`.
- [ ] Keep the **same `SECRET_KEY`**. Session cookies are checked against it, so people who are signed in stay signed
      in across the switch, and sign-ins work on both backends during the window.
- [ ] Relative paths (a SQLite URL, `REPORT_LOGO_FILE`, `WFO_MAPPING_FILE`) are resolved from the env file's folder.
      Check that the report logo path still points to the file.
- [ ] `APP_ENV=production`, Entra values (all four, or none), and `CORS_ORIGINS` exactly as for the Python backend.

## 3. Back up the database (go-ahead needed)

```powershell
$stamp = Get-Date -Format yyyyMMdd-HHmm
pg_dump -Fc -h localhost -U ats -d ats_db -f "ats_db-before-dotnet-$stamp.dump"   # password from the prompt or PGPASSWORD
pg_restore --list "ats_db-before-dotnet-$stamp.dump" | Select-Object -First 5        # the dump is readable
```

- [ ] Keep the dump outside the repository, next to the earlier backups.

## 4. Check the schema (read-only)

```powershell
cd C:\Dev\ATS\backend-dotnet
dotnet run --project src/ATS.Api -c Release -- schema-check
```

- [ ] It must say the schema **matches Alembic head 0006**. If it reports differences, stop. Do not continue until
      the differences are understood.

## 5. Adopt the database for EF Core (go-ahead needed)

```powershell
dotnet run --project src/ATS.Api -c Release -- ef-baseline
```

- [ ] It checks the schema again and changes nothing if it differs. When it matches, the only change is a new
      `__EFMigrationsHistory` table recording the baseline migration. No existing table or row is touched.
- [ ] Never run `migrate` on this database. `migrate` refuses an Alembic database anyway, and `ef-baseline` is the
      way to adopt one.

## 6. Start the .NET API on port 8001

Use one of:

```powershell
dotnet run --project src/ATS.Api -c Release                      # from backend-dotnet\, listens on 127.0.0.1:8001
docker build -t ats-api backend-dotnet                            # or the container (from the repository root)
docker run -d --name ats-api -p 127.0.0.1:8001:8001 --env-file <env file> ats-api
```

- [ ] The startup log names the database (`database: postgresql ats@host:5432/ats_db (env file: ...)`) and has no
      warnings.
- [ ] `GET http://127.0.0.1:8001/api/health` returns `ok`, and `GET /api/health/db` returns 200 with
      `"database": "postgresql"`.

## 7. Check it on the real data (read-only)

Sign in with a real administrator account and compare against the Python backend, which is still running on the
same database:

```powershell
$env:ATS_CHECK_EMAIL = "<an administrator's email>"   # the password is asked for and never shown
python backend-dotnet\tools\cutover_check.py --base http://127.0.0.1:8001 --compare http://127.0.0.1:8000
```

- [ ] All checks pass and every compared response is identical. The script covers health, sign-in and sign-out,
      attendance, dashboards, hierarchy, WFO, users, imports and CSV reports.
- [ ] Repeat with a manager's account and an employee's account if you have them. Each role reads a different set.
- [ ] The only writes are the session rows and the LOGIN / LOGOUT audit entries that any sign-in creates.

## 8. Switch the frontend

- [ ] Development server: set `VITE_DEV_BACKEND_URL=http://127.0.0.1:8001` in `frontend\.env`, then restart
      `npm run dev`.
- [ ] Reverse proxy (if one serves the built frontend): change the `/api` upstream from port 8000 to port 8001 and
      reload the proxy.
- [ ] In the browser, sign in. Then open the dashboard, the team and organisation views and the WFO view, and download
      one PDF and one Excel report.
- [ ] Optional: an administrator previews and discards a small timesheet file under **Import timesheets**.

## 9. Watch

- [ ] Watch the API log for a day: problems appear as `WARN` / `ERROR` lines. Unhandled errors carry the
      `request_id` that the error response also returns.
- [ ] Watch the audit log (the **Audit log** page): sign-ins, imports and exports should look as before.
- [ ] Leave the Python backend installed and stopped, with its `.venv` intact, until the agreed confirmation date.

## Rolling back

The two backends read and write the same tables in the same formats (the parity runs in the migration plan compared
writes table by table), so the data needs no conversion in either direction.

1. Point the frontend back to port 8000 (`VITE_DEV_BACKEND_URL`, or the proxy upstream) and start the Python backend
   if it was stopped.
2. Stop the .NET API.
3. Leave `__EFMigrationsHistory` in place. The Python backend never reads it, and Alembic ignores it (step 1).
4. Only if data was damaged, restore the step 3 dump. **This replaces the whole database. It needs an explicit
   go-ahead, and the damaged state should first be saved with its own `pg_dump`.**

   ```powershell
   pg_restore --clean --if-exists -h localhost -U ats -d ats_db "ats_db-before-dotnet-<stamp>.dump"
   ```

## After confirmation (separate go-ahead)

- [ ] Change the default in `frontend/vite.config.ts` and `frontend/.env.example` to `http://127.0.0.1:8001`.
- [ ] Remove the Python backend job from both pipelines, then remove `backend/` (with its scripts) and update the
      root README.
