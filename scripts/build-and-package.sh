#!/usr/bin/env bash
#
# build-and-package.sh — publish MT-Uptime for a Linux server and bundle it with the deploy assets.
#
# Run from anywhere:  ./scripts/build-and-package.sh [--arch x64|arm64] [--self-contained]
# Produces:           build/mt-uptime.tar.gz
#
# Copy that tarball to the server and run deploy/deploy-on-server.sh there.
#
#   --arch x64|arm64    the SERVER's CPU, not this machine's. Default x64. Use arm64 for a Raspberry Pi,
#                       AWS Graviton, Ampere or any other 64-bit ARM host. A build for one does not run
#                       on the other, and deploy-on-server.sh refuses a mismatch before touching anything.
#   --self-contained    bundle the .NET runtime into the build (~95 MB instead of ~5 MB)
#
# Framework-dependent by default: the target installs the ASP.NET Core runtime once (see
# deploy/README-deploy.md), which keeps the tarball small and lets security patches to the runtime
# arrive through the distribution's package manager rather than requiring a redeploy.
#
# Use --self-contained when installing a runtime on the target is undesirable: a shared host running
# other .NET applications, where `apt install aspnetcore-runtime-N` can replace the dotnet host package
# those applications depend on, or any machine where you do not want to touch system packages at all.
# The trade is that runtime security patches then arrive only when you rebuild and redeploy.

set -euo pipefail

SELF_CONTAINED=false
ARCH=x64
while [[ $# -gt 0 ]]; do
    case "$1" in
        --self-contained) SELF_CONTAINED=true; shift ;;
        --arch)           ARCH="${2:?--arch needs x64 or arm64}"; shift 2 ;;
        --arch=*)         ARCH="${1#--arch=}"; shift ;;
        -h|--help)        sed -n '2,22p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

# Not auto-detected on purpose: the machine building the tarball is routinely not the one running it
# (an x64 laptop building for a Pi, an ARM Mac building for an x64 VPS).
case "$ARCH" in
    x64|arm64) RID="linux-$ARCH" ;;
    *) echo "--arch must be x64 or arm64, not '$ARCH'" >&2; exit 1 ;;
esac

ENGINE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD="$ENGINE/build"
PUBLISH="$BUILD/publish"

rm -rf "$BUILD"
mkdir -p "$PUBLISH"

if [[ "$SELF_CONTAINED" == true ]]; then
    echo "==> publish SelfHost.MT-Uptime ($RID, SELF-CONTAINED — no runtime needed on the target)"
else
    echo "==> publish SelfHost.MT-Uptime ($RID, framework-dependent)"
fi
dotnet publish "$ENGINE/SelfHost.MT-Uptime" \
    -c Release \
    -r "$RID" \
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
echo "Done: $BUILD/mt-uptime.tar.gz ($RID)"
echo "Next: scp it to the server, then  sudo ./deploy-on-server.sh mt-uptime.tar.gz"
