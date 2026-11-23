<#
.SYNOPSIS
  Runs the API test-suite against PostgreSQL, including the PostgreSQL integration tests.

.DESCRIPTION
  Uses -DatabaseUrl, or $env:ATS_TEST_POSTGRES_URL, or (with -UseDocker) the docker compose database
  "ats_test" built from the root .env. The target database is WIPED by the tests, so its name must
  contain "test". Sets ATS_REQUIRE_POSTGRES=1, so missing PostgreSQL is an error, not a skip.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\test-postgres.ps1 -UseDocker
  powershell -ExecutionPolicy Bypass -File scripts\test-postgres.ps1 -DatabaseUrl "postgresql://ats:secret@db.example:5432/ats_test"
#>
[CmdletBinding()]
param(
    [string]$DatabaseUrl = $env:ATS_TEST_POSTGRES_URL,
    [switch]$UseDocker,
    [string[]]$TestArgs = @()
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Api = Join-Path $Root "backend-dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET SDK is not installed (see README)." }

if ($UseDocker) {
    $envFile = Join-Path $Root ".env"
    if (-not (Test-Path $envFile)) { throw "Root .env not found (copy .env.example and set POSTGRES_PASSWORD)." }
    $vars = @{}
    Get-Content $envFile | Where-Object { $_ -match '^\s*([A-Z_]+)\s*=\s*(.*)$' } | ForEach-Object { $vars[$Matches[1]] = $Matches[2].Trim() }
    Push-Location $Root
    try {
        & cmd /c "docker compose up -d db"
        if ($LASTEXITCODE -ne 0) { throw "docker compose up failed (is Docker Desktop running?)" }
        Write-Host "Waiting for PostgreSQL to become healthy..."
        for ($i = 0; $i -lt 60; $i++) {
            $state = & cmd /c "docker inspect -f {{.State.Health.Status}} ats-postgres 2>nul"
            if ($state -eq "healthy") { break }
            Start-Sleep -Seconds 1
        }
        if ($state -ne "healthy") { throw "PostgreSQL container did not become healthy" }
    } finally { Pop-Location }
    $user = if ($vars.POSTGRES_USER) { $vars.POSTGRES_USER } else { "ats" }
    $port = if ($vars.POSTGRES_PORT) { $vars.POSTGRES_PORT } else { "5432" }
    $pw = [uri]::EscapeDataString($vars.POSTGRES_PASSWORD)
    $DatabaseUrl = "postgresql://${user}:${pw}@localhost:${port}/ats_test"
}

if (-not $DatabaseUrl) { throw "Provide -DatabaseUrl, set ATS_TEST_POSTGRES_URL, or use -UseDocker." }

$env:ATS_TEST_POSTGRES_URL = $DatabaseUrl
$env:ATS_REQUIRE_POSTGRES = "1"
Push-Location $Api
try {
    & dotnet test ATS.sln @TestArgs
    exit $LASTEXITCODE
} finally {
    Pop-Location
    Remove-Item Env:ATS_TEST_POSTGRES_URL -ErrorAction SilentlyContinue
    Remove-Item Env:ATS_REQUIRE_POSTGRES -ErrorAction SilentlyContinue
}
