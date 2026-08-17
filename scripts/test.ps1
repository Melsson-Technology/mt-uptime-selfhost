# test.ps1 - run the full test suite.
#
# The suite is hermetic: throwaway SQLite files in the temp directory, no external services, no
# environment variables. A fresh clone with only the .NET SDK installed can run this immediately.
#
#   .\scripts\test.ps1              # all tests
#   .\scripts\test.ps1 --filter X   # any extra arguments pass through to `dotnet test`

$ErrorActionPreference = "Stop"
$engine = Split-Path -Parent $PSScriptRoot

dotnet test (Join-Path $engine "MT-Uptime.Engine.slnx") @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
