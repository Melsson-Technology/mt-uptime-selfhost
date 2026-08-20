#!/usr/bin/env bash
#
# deploy-on-server.sh — unpack a build tarball and swap it into /opt/mt-uptime.
#
# Copy mt-uptime.tar.gz to the server, then run as root:
#
#   sudo ./deploy-on-server.sh mt-uptime.tar.gz
#
# The previous publish directory is kept as publish.old. If the new build fails to start or fails its
# health check, this script puts that previous build back automatically and leaves the failed one at
# publish.failed for diagnosis — so a bad deploy ends with the service running, not with an operator
# holding a dead box and a rollback command. Run provision.sh first if this is a fresh server.
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

# Point the unit at whichever launcher this build actually needs.
#
# The shipped unit runs `/usr/bin/dotnet …/MT.Uptime.Web.dll`, which assumes a framework-dependent
# build and an installed runtime. Follow the documented advice for a shared host — build
# --self-contained, provision with SKIP_RUNTIME=1 so apt is never touched — and there is no
# /usr/bin/dotnet at all: systemd fails with 203/EXEC and a service that never starts. Our own
# production instance is in exactly that state and has been carrying a hand-written drop-in to fix
# it since day one; that knowledge never made it back into the scripts everyone else runs.
#
# So decide it here, where the build has just been unpacked and can be inspected, rather than asking
# the operator to know. libcoreclr.so is the marker: it ships only with a self-contained publish.
# Switching build types either way is handled, because the drop-in is rewritten or removed on every
# deploy rather than only created once.
DROPIN_DIR="/etc/systemd/system/${SERVICE}.service.d"
DROPIN="$DROPIN_DIR/10-apphost.conf"
if [[ -f "$APP_HOME/publish/libcoreclr.so" && -f "$APP_HOME/publish/MT.Uptime.Web" ]]; then
    echo "==> self-contained build — running its own apphost, no system dotnet needed"
    mkdir -p "$DROPIN_DIR"
    # The empty ExecStart= is required: systemd appends to a list otherwise, and the unit would try
    # both launchers in turn.
    printf '%s\n' \
        '# Written by deploy-on-server.sh: this build is self-contained and must not use the muxer.' \
        '[Service]' \
        'ExecStart=' \
        "ExecStart=$APP_HOME/publish/MT.Uptime.Web" \
        > "$DROPIN"
    systemctl daemon-reload
elif [[ -f "$DROPIN" ]]; then
    echo "==> framework-dependent build — removing the self-contained apphost override"
    rm -f "$DROPIN"
    systemctl daemon-reload
fi

# Put the previous build back, and keep the failed one for inspection.
#
# This used to be three commands printed for the operator to run. That was actively unsafe, and running
# it proved so: the publish -> publish.old rotation happens before the service is started, so a *failed*
# deploy still consumes the rollback slot. Deploy a broken build twice — which is precisely what someone
# does when the first attempt fails — and the second failure moves the first failure's broken build into
# publish.old, on top of the last good one. The printed rollback then faithfully restores a broken build
# and leaves nothing to try again. Demonstrated end to end on a scratch box: two failed deploys, and the
# documented recovery produced a dead service with no rollback target left at all.
#
# Doing it here instead keeps the invariant true no matter how many times a bad build is deployed: the
# good build always ends up back in publish, so the next attempt rotates the good one aside again.
restore_previous() {
    if [[ ! -d "$APP_HOME/publish.old" ]]; then
        echo "There is no previous build to restore — publish.old does not exist." >&2
        echo "This is expected on a first deploy. Fix the build and deploy again." >&2
        return 1
    fi

    echo "==> rolling back to the previous build" >&2
    systemctl stop "$SERVICE" 2>/dev/null || true

    # Kept rather than deleted: whatever went wrong is in here, and it is the only copy.
    rm -rf "$APP_HOME/publish.failed"
    mv "$APP_HOME/publish" "$APP_HOME/publish.failed"
    mv "$APP_HOME/publish.old" "$APP_HOME/publish"

    if systemctl start "$SERVICE"; then
        echo "Rolled back: the previous build is running again." >&2
        echo "The build that failed is at $APP_HOME/publish.failed for diagnosis." >&2
        echo "  journalctl -u $SERVICE -n 50 --no-pager" >&2
        return 0
    fi

    echo "ROLLBACK ALSO FAILED — the previous build did not start either." >&2
    echo "  journalctl -u $SERVICE -n 50 --no-pager" >&2
    return 1
}

echo "==> starting $SERVICE"
# Checked rather than left to `set -e`. The unit is Type=notify, so a build that cannot start makes
# `systemctl start` itself fail — and an unchecked failure here kills the script before any of the
# guidance below is printed, handing back a bare exit code, a dead service, and no mention that
# publish.old exists. Verified by deploying a deliberately corrupted build.
if ! systemctl start "$SERVICE"; then
    echo >&2
    echo "FAILED: $SERVICE did not start, so this build is not running." >&2
    restore_previous || true
    exit 1
fi

# Give it a moment to migrate the database and bind, then prove it actually came up. A deploy that
# silently fails health is worse than one that fails loudly.
sleep 3
# Health-check the port the service actually listens on. The unit defaults to 5081 but an operator can
# move it in /etc/mt-uptime/mt-uptime.env (any fixed default is eventually taken on a shared host), and
# probing the wrong port would report a perfectly healthy deploy as a failure.
#
# Both guards below are load-bearing under `set -euo pipefail`, and neither is hypothetical — this
# aborted the script with a bare "exit 2" on the very first clean install ever attempted, immediately
# after a *successful* start, before it could print so much as a reason:
#
#   - The file may not exist. grep exits 2 for a missing file; 2>/dev/null hides the message and not
#     the status, pipefail promotes it out of the pipeline, and set -e then kills the script.
#   - The file may exist without an ASPNETCORE_URLS line, which is grep's exit 1. Same ending.
#
# This never showed up on the instance we run because that one was provisioned by hand, complete with
# an env file. It would have hit every single person who followed the documented path instead.
HEALTH_URL=""
if [[ -r /etc/mt-uptime/mt-uptime.env ]]; then
    HEALTH_URL="$(
        grep -hoP '^\s*ASPNETCORE_URLS=\K\S+' /etc/mt-uptime/mt-uptime.env \
        | tail -1 | cut -d';' -f1 || true
    )"
fi
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:5081}"
echo "==> health check ${HEALTH_URL%/}/healthz"
if curl -fsS --max-time 10 "${HEALTH_URL%/}/healthz" >/dev/null 2>&1; then
    echo
    echo "Deployed and healthy. Previous build kept at $APP_HOME/publish.old"
else
    echo
    echo "WARNING: deployed, but /healthz did not respond." >&2
    restore_previous || true
    exit 1
fi
