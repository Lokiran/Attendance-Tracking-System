<#
.SYNOPSIS
  Starts the backend with the explicit local SQLite profile (backend\.env.sqlite).

.DESCRIPTION
  * Selects the profile via ATS_ENV_FILE; nothing falls back to SQLite implicitly.
  * Never creates, resets or overwrites a database unless -Init is given. -Init runs
    `alembic upgrade head` (creates the file if missing, otherwise applies pending migrations only).
  * -Seed (with -Init) loads demo data; it only adds missing demo people/attendance.
  * Refuses to start if DATABASE_URL is set in the environment (it would override the profile)
    or if the port is already in use (it never stops other processes).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1
  powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1 -Init -Seed
  powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1 -Port 8001 -Reload
#>
[CmdletBinding()]
param(
    [int]$Port = 8000,
    [switch]$Init,
    [switch]$Seed,
    [switch]$Reload
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Backend = Join-Path $Root "backend"
$Python = Join-Path $Backend ".venv\Scripts\python.exe"
$Alembic = Join-Path $Backend ".venv\Scripts\alembic.exe"
$EnvProfile = Join-Path $Backend ".env.sqlite"

if (-not (Test-Path $Python)) { throw "backend\.venv not found. Run scripts\setup.ps1 first." }
if (-not (Test-Path $EnvProfile)) {
    throw "backend\.env.sqlite not found. Create it: copy backend\.env.sqlite.example backend\.env.sqlite, then set SECRET_KEY (and SEED_* passwords if you will seed)."
}
if ($env:DATABASE_URL) {
    throw "DATABASE_URL is set in this shell and would override backend\.env.sqlite. Run 'Remove-Item Env:DATABASE_URL' first."
}
if ($Seed -and -not $Init) { throw "-Seed requires -Init." }

$busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($busy) {
    $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($busy.OwningProcess)"
    throw "Port $Port is already in use by PID $($busy.OwningProcess) ($($owner.CommandLine)). Stop it yourself or use -Port."
}

$env:ATS_ENV_FILE = ".env.sqlite"
Push-Location $Backend
try {
    if ($Init) {
        Write-Host "Applying migrations (alembic upgrade head)..." -ForegroundColor Cyan
        & $Alembic upgrade head
        if ($LASTEXITCODE -ne 0) { throw "alembic upgrade failed" }
        if ($Seed) {
            & $Python -m app.cli seed
            if ($LASTEXITCODE -ne 0) { throw "seed failed" }
        }
    }
    $uvicornArgs = @("-m", "uvicorn", "app.main:app", "--host", "127.0.0.1", "--port", "$Port")
    if ($Reload) { $uvicornArgs += "--reload" }
    Write-Host "Starting backend with backend\.env.sqlite on http://127.0.0.1:$Port (API docs: /api/docs)" -ForegroundColor Cyan
    & $Python @uvicornArgs
} finally {
    Pop-Location
    Remove-Item Env:ATS_ENV_FILE -ErrorAction SilentlyContinue
}
