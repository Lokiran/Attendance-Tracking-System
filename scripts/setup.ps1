<#
.SYNOPSIS
  Development-only setup helper for the Office Attendance Tracker (Windows PowerShell 5.1+).

.DESCRIPTION
  Checks prerequisites and ports, creates the Python virtual environment, installs backend and
  frontend dependencies, and creates backend/.env and frontend/.env from the examples with a random
  SECRET_KEY. Existing .env files are never overwritten.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -CheckOnly
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -DbPassword "my-local-db-password"
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$DbPassword = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Backend = Join-Path $Root "backend"
$Frontend = Join-Path $Root "frontend"
$problems = 0

function Ok($m) { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }
function Bad($m) { Write-Host "  [fail] $m" -ForegroundColor Red; $script:problems++ }

function New-Secret([int]$bytes = 36) {
    $buf = New-Object byte[] $bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buf)
    return ([Convert]::ToBase64String($buf) -replace '[+/=]', 'x')
}

Write-Host "`nChecking prerequisites" -ForegroundColor Cyan
$python = Get-Command python -ErrorAction SilentlyContinue
if ($python) {
    $v = (& python -c "import sys; print('%d.%d' % sys.version_info[:2])")
    if ([version]$v -ge [version]"3.11") { Ok "Python $v" } else { Bad "Python $v found; 3.11+ required" }
} else { Bad "Python not found (install 3.11+ from python.org)" }

$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    $nv = (& node --version).TrimStart("v")
    if ([version]$nv -ge [version]"20.19") { Ok "Node.js $nv" } else { Bad "Node.js $nv found; 20.19+ required" }
} else { Bad "Node.js not found (install the LTS release from nodejs.org)" }
if (Get-Command npm -ErrorAction SilentlyContinue) { Ok "npm $(& npm --version)" } else { Bad "npm not found" }

if (Get-Command docker -ErrorAction SilentlyContinue) {
    # Via cmd so PowerShell 5.1 does not turn docker's stderr into a terminating error.
    & cmd /c "docker info >nul 2>&1"
    if ($LASTEXITCODE -eq 0) { Ok "Docker daemon running (optional, for PostgreSQL)" }
    else { Warn "Docker installed but the daemon is not running; start Docker Desktop or use a local PostgreSQL" }
} elseif (Get-Command psql -ErrorAction SilentlyContinue) { Ok "psql found (local PostgreSQL)" }
else { Warn "Neither Docker nor psql found: install PostgreSQL 15+ or Docker Desktop" }

Write-Host "`nChecking ports" -ForegroundColor Cyan
foreach ($port in 5432, 8000, 5173) {
    $busy = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    if ($busy) {
        $proc = (Get-Process -Id ($busy | Select-Object -First 1).OwningProcess -ErrorAction SilentlyContinue).ProcessName
        if ($port -eq 5432) { Warn "Port 5432 in use by '$proc' (fine if that is your PostgreSQL)" }
        else { Warn "Port $port in use by '$proc'; stop it or change the port (see README troubleshooting)" }
    } else { Ok "Port $port free" }
}

if ($CheckOnly) {
    if ($problems) { Write-Host "`n$problems blocking problem(s) found." -ForegroundColor Red; exit 1 }
    Write-Host "`nPrerequisites look good." -ForegroundColor Green; exit 0
}
if ($problems) { Write-Host "`nFix the problems above and re-run." -ForegroundColor Red; exit 1 }

Write-Host "`nBackend" -ForegroundColor Cyan
$venvPy = Join-Path $Backend ".venv\Scripts\python.exe"
if (-not (Test-Path $venvPy)) { & python -m venv (Join-Path $Backend ".venv"); Ok "Created backend\.venv" } else { Ok "backend\.venv exists" }
& $venvPy -m pip install --quiet --upgrade pip
& $venvPy -m pip install --quiet -r (Join-Path $Backend "requirements.txt")
if ($LASTEXITCODE -ne 0) { Bad "pip install failed"; exit 1 }
Ok "Python dependencies installed"

$envFile = Join-Path $Backend ".env"
if (-not (Test-Path $envFile)) {
    $content = Get-Content (Join-Path $Backend ".env.example") -Raw
    $content = $content -replace "SECRET_KEY=.*", ("SECRET_KEY=" + (New-Secret 48))
    if ($DbPassword) { $content = $content -replace "CHANGE_ME_DB_PASSWORD", $DbPassword }
    $content = $content -replace "SEED_ADMIN_PASSWORD=.*", ("SEED_ADMIN_PASSWORD=Adm" + (New-Secret 12) + "9")
    $content = $content -replace "SEED_DEMO_PASSWORD=.*", ("SEED_DEMO_PASSWORD=Demo" + (New-Secret 12) + "7")
    Set-Content -Path $envFile -Value $content -Encoding UTF8
    Ok "Created backend\.env with a random SECRET_KEY and random demo passwords (see the file)"
    if (-not $DbPassword) { Warn "Edit DATABASE_URL in backend\.env: replace CHANGE_ME_DB_PASSWORD with your PostgreSQL password" }
} else { Ok "backend\.env exists (left unchanged)" }

$rootEnv = Join-Path $Root ".env"
if (-not (Test-Path $rootEnv) -and $DbPassword) {
    (Get-Content (Join-Path $Root ".env.example") -Raw) -replace "CHANGE_ME_DB_PASSWORD", $DbPassword | Set-Content -Path $rootEnv -Encoding UTF8
    Ok "Created root .env for docker compose"
}

Write-Host "`nFrontend" -ForegroundColor Cyan
Push-Location $Frontend
try {
    & npm install --no-fund --no-audit
    if ($LASTEXITCODE -ne 0) { Bad "npm install failed"; exit 1 }
    Ok "npm dependencies installed"
    if (-not (Test-Path ".env")) { Copy-Item ".env.example" ".env"; Ok "Created frontend\.env" }
} finally { Pop-Location }

Write-Host "`nNext steps (see README.md):" -ForegroundColor Cyan
Write-Host "  1. Start PostgreSQL:   docker compose up -d db      (or use your local PostgreSQL)"
Write-Host "  2. Migrate + seed:     cd backend; .\.venv\Scripts\alembic.exe upgrade head; .\.venv\Scripts\python.exe -m app.cli seed"
Write-Host "  3. Backend:            .\.venv\Scripts\python.exe -m uvicorn app.main:app --reload --port 8000"
Write-Host "  4. Frontend:           cd frontend; npm run dev   ->  http://localhost:5173"
