# run.ps1 - start MT-Uptime locally on http://localhost:5081
#
# First run creates the database and shows the setup wizard. Data lives in
# SelfHost.MT-Uptime\App_Data\ (git-ignored) - delete that directory to start over, but note it holds
# the Data Protection keys as well as the database, so deleting it discards any stored secrets too.
#
#   .\scripts\run.ps1                    # Development
#   .\scripts\run.ps1 -c Release         # extra arguments pass through to `dotnet run`

$ErrorActionPreference = "Stop"
$engine = Split-Path -Parent $PSScriptRoot

Write-Host "Starting MT-Uptime -> http://localhost:5081  (Ctrl+C to stop)"
dotnet run --project (Join-Path $engine "SelfHost.MT-Uptime") @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
