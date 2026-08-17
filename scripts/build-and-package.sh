#!/usr/bin/env bash
#
# build-and-package.sh — publish MT-Uptime for linux-x64 and bundle it with the deploy assets.
#
# Run from anywhere:  ./scripts/build-and-package.sh
# Produces:           build/mt-uptime.tar.gz
#
# Copy that tarball to the server and run deploy/deploy-on-server.sh there.
#
# Framework-dependent by default: the target installs the ASP.NET Core runtime once (see
# deploy/README-deploy.md), which keeps the tarball small and lets security patches to the runtime
# arrive through the distribution's package manager rather than requiring a redeploy.
#
#   --self-contained    bundle the .NET runtime into the build (~95 MB instead of ~5 MB)
#
# Use --self-contained when installing a runtime on the target is undesirable: a shared host running
# other .NET applications, where `apt install aspnetcore-runtime-N` can replace the dotnet host package
# those applications depend on, or any machine where you do not want to touch system packages at all.
# The trade is that runtime security patches then arrive only when you rebuild and redeploy.

set -euo pipefail

SELF_CONTAINED=false
for arg in "$@"; do
    case "$arg" in
        --self-contained) SELF_CONTAINED=true ;;
        -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
        *) echo "unknown option: $arg" >&2; exit 1 ;;
    esac
done

ENGINE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD="$ENGINE/build"
PUBLISH="$BUILD/publish"

rm -rf "$BUILD"
mkdir -p "$PUBLISH"

if [[ "$SELF_CONTAINED" == true ]]; then
    echo "==> publish SelfHost.MT-Uptime (linux-x64, SELF-CONTAINED — no runtime needed on the target)"
else
    echo "==> publish SelfHost.MT-Uptime (linux-x64, framework-dependent)"
fi
dotnet publish "$ENGINE/SelfHost.MT-Uptime" \
    -c Release \
    -r linux-x64 \
    --self-contained "$SELF_CONTAINED" \
    -o "$PUBLISH"

echo "==> bundle deploy assets"
cp -r "$ENGINE/deploy" "$BUILD/deploy"

# Never ship developer state. App_Data holds the local SQLite database AND the Data Protection keys;
# shipping it would overwrite the server's database and leak the keys that decrypt every stored secret.
find "$BUILD" -depth -type d -name 'App_Data*' -exec rm -rf {} + 2>/dev/null || true
find "$BUILD" -type f \( -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' \) -delete 2>/dev/null || true

echo "==> tar"
tar -czf "$BUILD/mt-uptime.tar.gz" -C "$BUILD" publish deploy

echo
echo "Done: $BUILD/mt-uptime.tar.gz"
echo "Next: scp it to the server, then  sudo ./deploy-on-server.sh mt-uptime.tar.gz"
