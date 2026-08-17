# build-and-package.ps1 - publish MT-Uptime for linux-x64 and bundle it with the deploy assets.
#
# Run from anywhere:  .\scripts\build-and-package.ps1
# Produces:           build\mt-uptime.tar.gz
#
# Copy that tarball to the server and run deploy/deploy-on-server.sh there.
#
# Framework-dependent by default: the target installs the ASP.NET Core runtime once (see
# deploy/README-deploy.md), which keeps the tarball small and lets security patches to the runtime
# arrive through the distribution's package manager rather than requiring a redeploy.
#
#   -SelfContained    bundle the .NET runtime into the build (~50 MB instead of ~5 MB)
#
# Use -SelfContained when installing a runtime on the target is undesirable: a shared host running
# other .NET applications, where `apt install aspnetcore-runtime-N` can replace the dotnet host package
# those applications depend on, or any machine where you do not want to touch system packages at all.
# The trade is that runtime security patches then arrive only when you rebuild and redeploy.
#
# `tar` ships with Windows 10 build 17063 and later, so no extra tooling is needed.

param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$engine  = Split-Path -Parent $PSScriptRoot
$build   = Join-Path $engine "build"
$publish = Join-Path $build  "publish"

if (Test-Path $build) { Remove-Item -Recurse -Force $build }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

if ($SelfContained) {
    Write-Host "==> publish SelfHost.MT-Uptime (linux-x64, SELF-CONTAINED - no runtime needed on the target)"
} else {
    Write-Host "==> publish SelfHost.MT-Uptime (linux-x64, framework-dependent)"
}
dotnet publish (Join-Path $engine "SelfHost.MT-Uptime") `
    -c Release `
    -r linux-x64 `
    --self-contained $(if ($SelfContained) { "true" } else { "false" }) `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> bundle deploy assets"
Copy-Item -Recurse (Join-Path $engine "deploy") (Join-Path $build "deploy")

# Never ship developer state. App_Data holds the local SQLite database AND the Data Protection keys;
# shipping it would overwrite the server's database and leak the keys that decrypt every stored secret.
Get-ChildItem $build -Recurse -Force -Directory -Filter "App_Data*" |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $build -Recurse -Force -File -Include "*.db", "*.db-wal", "*.db-shm" |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "==> tar"
tar -czf (Join-Path $build "mt-uptime.tar.gz") -C $build publish deploy
if ($LASTEXITCODE -ne 0) { throw "tar failed" }

Write-Host ""
Write-Host "Done: $(Join-Path $build 'mt-uptime.tar.gz')"
Write-Host "Next: scp it to the server, then  sudo ./deploy-on-server.sh mt-uptime.tar.gz"
