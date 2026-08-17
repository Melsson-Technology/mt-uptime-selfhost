#!/usr/bin/env bash
#
# deploy-on-server.sh — unpack a build tarball and swap it into /opt/mt-uptime.
#
# Copy mt-uptime.tar.gz to the server, then run as root:
#
#   sudo ./deploy-on-server.sh mt-uptime.tar.gz
#
# The previous publish directory is kept as publish.old, so a rollback is one `mv` away. Run
# provision.sh first if this is a fresh server.
#
# Application data is NOT touched: the database and Data Protection keys live in /var/lib/mt-uptime
# (set by the systemd unit), deliberately outside the deploy directory, so redeploying can never
# destroy them. Pending EF migrations apply automatically on the next start.

set -euo pipefail

TARBALL="${1:?usage: deploy-on-server.sh <tarball>}"
APP_HOME="/opt/mt-uptime"
SERVICE="mt-uptime"
SERVICE_USER="mtuptime"

[[ -f "$TARBALL" ]] || { echo "no such file: $TARBALL" >&2; exit 1; }
[[ $EUID -eq 0 ]]   || { echo "must run as root (try: sudo $0 $TARBALL)" >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> unpacking"
tar -xzf "$TARBALL" -C "$STAGE"
[[ -d "$STAGE/publish" ]] || { echo "tarball has no publish/ directory — wrong file?" >&2; exit 1; }

# Sanity check before touching a running service: the unit executes this assembly by name.
[[ -f "$STAGE/publish/MT.Uptime.Web.dll" ]] || {
    echo "publish/MT.Uptime.Web.dll missing — refusing to deploy a broken build" >&2; exit 1; }

mkdir -p "$APP_HOME"

echo "==> stopping $SERVICE"
systemctl stop "$SERVICE" 2>/dev/null || true

echo "==> swapping publish directory"
if [[ -d "$APP_HOME/publish" ]]; then
    rm -rf "$APP_HOME/publish.old"
    mv "$APP_HOME/publish" "$APP_HOME/publish.old"
fi
mv "$STAGE/publish" "$APP_HOME/publish"

# Keep the deploy assets alongside the app so the next deploy/rollback has them locally.
rm -rf "$APP_HOME/deploy"
cp -r "$STAGE/deploy" "$APP_HOME/deploy" 2>/dev/null || true

chown -R "$SERVICE_USER:$SERVICE_USER" "$APP_HOME"

# A self-contained build has a native apphost next to the .dll. Packaging on Windows loses the execute
# bit (NTFS has no concept of one), so the file arrives 644 and systemd fails with "Permission denied"
# — a confusing way to discover a file-mode problem. Harmless for framework-dependent builds, where
# this file simply does not exist.
if [[ -f "$APP_HOME/publish/MT.Uptime.Web" ]]; then
    chmod +x "$APP_HOME/publish/MT.Uptime.Web"
fi

echo "==> starting $SERVICE"
systemctl start "$SERVICE"

# Give it a moment to migrate the database and bind, then prove it actually came up. A deploy that
# silently fails health is worse than one that fails loudly.
sleep 3
# Health-check the port the service actually listens on. The unit defaults to 5081 but an operator can
# move it in /etc/mt-uptime/mt-uptime.env (any fixed default is eventually taken on a shared host), and
# probing the wrong port would report a perfectly healthy deploy as a failure.
HEALTH_URL="$(
    grep -hoP '^\s*ASPNETCORE_URLS=\K\S+' /etc/mt-uptime/mt-uptime.env 2>/dev/null \
    | tail -1 | cut -d';' -f1
)"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:5081}"
echo "==> health check ${HEALTH_URL%/}/healthz"
if curl -fsS --max-time 10 "${HEALTH_URL%/}/healthz" >/dev/null 2>&1; then
    echo
    echo "Deployed and healthy. Previous build kept at $APP_HOME/publish.old"
else
    echo
    echo "WARNING: deployed, but /healthz did not respond." >&2
    echo "  journalctl -u $SERVICE -n 50 --no-pager" >&2
    echo "  rollback:  sudo systemctl stop $SERVICE && sudo rm -rf $APP_HOME/publish && \\" >&2
    echo "             sudo mv $APP_HOME/publish.old $APP_HOME/publish && sudo systemctl start $SERVICE" >&2
    exit 1
fi
