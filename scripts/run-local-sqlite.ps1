<#
.SYNOPSIS
  Starts the API with the explicit local SQLite profile (backend-dotnet\.env.sqlite).

.DESCRIPTION
  * Selects the profile via ATS_ENV_FILE; nothing falls back to SQLite implicitly.
  * Never creates, resets or overwrites a database unless -Init is given. -Init runs the `migrate`
    command: it creates the file if it is missing and applies pending EF migrations. A database created
    by the former Python backend (Alembic) is refused by `migrate`; adopt it once with
    `dotnet run --project src\ATS.Api -- ef-baseline` (it only adds the EF history table).
  * -Seed (with -Init) loads demo data; it only adds missing demo people and attendance.
  * Refuses to start if DATABASE_URL is set in the environment (it would override the profile)
    or if the port is already in use (it never stops other processes).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1
  powershell -ExecutionPolicy Bypass -File scripts\run-local-sqlite.ps1 -Init -Seed
#>
[CmdletBinding()]
param(
    [int]$Port = 8001,
    [switch]$Init,
    [switch]$Seed
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Api = Join-Path $Root "backend-dotnet"
$EnvProfile = Join-Path $Api ".env.sqlite"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET SDK is not installed (see README)." }
if (-not (Test-Path $EnvProfile)) {
    throw "backend-dotnet\.env.sqlite not found. Create it: copy backend-dotnet\.env.sqlite.example backend-dotnet\.env.sqlite, then set SECRET_KEY (and SEED_* passwords if you will seed)."
}
if ($env:DATABASE_URL) {
    throw "DATABASE_URL is set in this shell and would override backend-dotnet\.env.sqlite. Run 'Remove-Item Env:DATABASE_URL' first."
}
if ($Seed -and -not $Init) { throw "-Seed requires -Init." }

$busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($busy) {
    $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($busy.OwningProcess)"
    throw "Port $Port is already in use by PID $($busy.OwningProcess) ($($owner.CommandLine)). Stop it yourself or use -Port."
}

$env:ATS_ENV_FILE = ".env.sqlite"
Push-Location $Api
try {
    if ($Init) {
        Write-Host "Applying migrations (migrate)..." -ForegroundColor Cyan
        & dotnet run --project src\ATS.Api -- migrate
        if ($LASTEXITCODE -ne 0) { throw "migrate refused or failed (see the message above)" }
        if ($Seed) {
            & dotnet run --project src\ATS.Api -- seed
            if ($LASTEXITCODE -ne 0) { throw "seed failed" }
        }
    }
    $env:URLS = "http://127.0.0.1:$Port"
    Write-Host "Starting the API with backend-dotnet\.env.sqlite on http://127.0.0.1:$Port (API docs: /api/docs)" -ForegroundColor Cyan
    & dotnet run --project src\ATS.Api
} finally {
    Pop-Location
    Remove-Item Env:ATS_ENV_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:URLS -ErrorAction SilentlyContinue
}
