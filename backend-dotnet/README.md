# Office Attendance Tracker API (.NET)

ASP.NET Core port of the FastAPI backend in `../backend`. The React frontend works unchanged; only its proxy target
moves from port 8000 to 8001 at cutover. Plan, decisions and progress: [`../docs/dotnet-migration-plan.md`](../docs/dotnet-migration-plan.md).

> **Status: phase 1 of 9** (scaffold, configuration, logging, OpenAPI, health, request pipeline). The Python backend
> remains the working backend until cutover is approved.

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

## Build and test

```powershell
cd backend-dotnet
dotnet build ATS.sln          # warnings are errors
dotnet test ATS.sln
```

The tests never read a developer env file (`ATS_ENV_FILE` points at an empty file during the run) and use
temporary SQLite files.
