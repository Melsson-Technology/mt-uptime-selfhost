#!/usr/bin/env bash
#
# install-mt-uptime.sh — replay deploy/README-deploy.md's "short version" for a re-run.
#
#   sudo ./install-mt-uptime.sh <hostname> [--skip-sdk] [--skip-build] [--package <tar.gz>]
#
# ─────────────────────────────────────────────────────────────────────────────────────────────────
#  DO NOT USE THIS FOR THE FIRST INSTALL ON A BOX
#
#  The first pass is done BY HAND, following deploy/README-deploy.md literally, because that is the
#  test: the README is the product's install instructions, and every command in it that misbehaves is
#  a finding. Walking two shell scripts by hand is how the S2 pass found twelve defects no test could
#  reach. A script that papers over a broken step also hides it.
#
#  This exists for the second install onward — after the battery has found something, it is fixed on
#  main, and the box needs the new build. At that point the README has already been walked and
#  replaying it is just cost.
#
#  It is a REPLAY, not an improvement. Every command below is the README's own, in the README's
#  order, with no fixes applied. If one of them needs a workaround, that workaround belongs in the
#  README and in a finding — not here, where it would silently stop being reproducible.
# ─────────────────────────────────────────────────────────────────────────────────────────────────
#
#   <hostname>       passed to provision.sh; becomes nginx's server_name. The box's public DNS name
#                    on EC2, or its private one — the battery never resolves it, so either works.
#   --skip-sdk       do not install the .NET SDK (it is already on PATH).
#   --skip-build     do not run build-and-package.sh; use an existing build/mt-uptime.tar.gz.
#   --package <p>    deploy this package instead of build/mt-uptime.tar.gz.
#
# Deliberately NOT done, matching the runbook:
#   * certbot — the battery is plain HTTP on :80. TLS is nginx's business and is not under test.
#   * App__PublicBaseUrl — must stay unset. Setting it narrows AllowedHosts to the declared host plus
#     loopback, and smoke.sh's "Host: <the box's own name>" check is what catches it having been set.
# ─────────────────────────────────────────────────────────────────────────────────────────────────

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENGINE="$(cd "$HERE/.." && pwd)"

HOSTNAME_ARG=""
SKIP_SDK=0
SKIP_BUILD=0
PACKAGE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-sdk)   SKIP_SDK=1; shift ;;
        --skip-build) SKIP_BUILD=1; shift ;;
        --package)    PACKAGE="${2:?--package needs a path}"; shift 2 ;;
        --package=*)  PACKAGE="${1#--package=}"; shift ;;
        -h|--help)    sed -n '2,34p' "$0"; exit 0 ;;
        -*)           echo "unknown option: $1" >&2; echo "try: $0 --help" >&2; exit 1 ;;
        *)
            if [[ -n "$HOSTNAME_ARG" ]]; then
                echo "unexpected argument: $1 (the hostname is already '$HOSTNAME_ARG')" >&2
                exit 1
            fi
            HOSTNAME_ARG="$1"; shift ;;
    esac
done

if [[ -z "$HOSTNAME_ARG" ]]; then
    {
        echo "REFUSING: no hostname given."
        echo
        echo "provision.sh writes it into nginx's server_name, and it has no sensible default."
        echo "    sudo $0 \$(hostname -f)"
    } >&2
    exit 1
fi

[[ $EUID -eq 0 ]] || { echo "must run as root (try: sudo $0 $*)" >&2; exit 1; }

# The SDK belongs to the invoking user, not to root: dotnet-install.sh puts it in $HOME/.dotnet, and
# under sudo that is root's home, where the test user cannot reach it. Resolve the real account.
REAL_USER="${SUDO_USER:-root}"
REAL_HOME="$(getent passwd "$REAL_USER" | cut -d: -f6)"

echo "==> replaying deploy/README-deploy.md's short version"
echo "    engine:   $ENGINE"
echo "    hostname: $HOSTNAME_ARG"
echo "    build as: $REAL_USER"
echo

# --- 1. the SDK -----------------------------------------------------------------------------------
#
# dotnet-install.sh rather than apt. Ubuntu's dotnet-sdk-10.0 may sit on a feature band older than
# global.json asks for (10.0.302, rollForward latestFeature), and the failure is a restore error that
# reads like a broken NuGet feed. The SERVICE does not use this SDK — provision.sh installs
# aspnetcore-runtime-10.0 from apt for that — this is only for building the package.

if [[ $SKIP_SDK -eq 0 && $SKIP_BUILD -eq 0 ]]; then
    if runuser -u "$REAL_USER" -- bash -lc 'command -v dotnet >/dev/null 2>&1'; then
        echo "==> .NET SDK: already on $REAL_USER's PATH ($(runuser -u "$REAL_USER" -- bash -lc 'dotnet --version'))"
    else
        echo "==> .NET SDK"
        runuser -u "$REAL_USER" -- bash -lc '
            set -euo pipefail
            curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
            bash /tmp/dotnet-install.sh --channel 10.0
            rm -f /tmp/dotnet-install.sh'
        echo "    installed to $REAL_HOME/.dotnet — add it to PATH in your own shell:"
        echo "        export PATH=\$HOME/.dotnet:\$PATH"
    fi
fi

# --- 2. build and package -------------------------------------------------------------------------

PACKAGE="${PACKAGE:-$ENGINE/build/mt-uptime.tar.gz}"

if [[ $SKIP_BUILD -eq 0 ]]; then
    echo "==> ./scripts/build-and-package.sh"
    # As the ordinary user: the build writes obj/ and bin/ into the tree, and doing that as root
    # leaves root-owned artefacts that break the next non-sudo build with a permissions error that
    # names no cause.
    runuser -u "$REAL_USER" -- bash -lc "
        set -euo pipefail
        export PATH=\"$REAL_HOME/.dotnet:\$PATH\"
        cd '$ENGINE'
        ./scripts/build-and-package.sh"
fi

[[ -f "$PACKAGE" ]] || { echo "no package at $PACKAGE" >&2; exit 1; }

# The README's own integrity check. A truncated tarball unpacks far enough to look plausible and then
# leaves an install missing exactly the files nobody notices until a page is opened.
echo "==> gzip -t $PACKAGE"
gzip -t "$PACKAGE"
echo "    intact ($(du -h "$PACKAGE" | cut -f1))"

# --- 3. provision ----------------------------------------------------------------------------------

echo "==> ./deploy/provision.sh $HOSTNAME_ARG"
"$ENGINE/deploy/provision.sh" "$HOSTNAME_ARG"

# --- 4. deploy ---------------------------------------------------------------------------------------

echo "==> ./deploy/deploy-on-server.sh $PACKAGE"
"$ENGINE/deploy/deploy-on-server.sh" "$PACKAGE"

# --- 5. what the operator does next --------------------------------------------------------------

echo
if [[ -f /var/lib/mt-uptime/setup-token ]]; then
    cat <<EOF
Installed, and first-run setup is OPEN. Do NOT complete it in a browser — smoke.sh completes it and
records the credentials in the target manifest, which is the only way the UI tier ever gets them:

  ./e2e/smoke.sh
EOF
else
    cat <<EOF
Installed. First-run setup is already complete on this database, so the wizard is closed and
smoke.sh will sign in with the credentials it stored last time:

  ./e2e/smoke.sh
EOF
fi

cat <<EOF

Certbot was deliberately not run and App__PublicBaseUrl was deliberately left unset. If either has
been changed by hand, smoke.sh's "Host: <the box's own name>" check is what will notice.
EOF
