#!/usr/bin/env bash
#
# provision.sh — prepare a fresh Debian/Ubuntu server to run MT-Uptime.
#
#   sudo ./provision.sh [hostname]
#
# Creates the service user and state directory, installs the ASP.NET Core runtime and nginx if they are
# needed, and lays down the systemd unit and reverse-proxy config. It does NOT deploy the application —
# run deploy-on-server.sh with a build tarball afterwards.
#
# Idempotent: safe to re-run. It will not overwrite an existing nginx site or clobber /var/lib/mt-uptime.
#
# Deliberately conservative about the packages already on the host, because this may not be a box
# dedicated to MT-Uptime:
#
#   * It REFUSES to install the ASP.NET Core runtime if apt would remove anything to make room —
#     on Ubuntu 24.04 that means dotnet-host-8.0, i.e. /usr/bin/dotnet, which every other .NET
#     application on the machine needs. Run with SKIP_RUNTIME=1 if you are deploying a
#     --self-contained build (the usual answer on a shared host: it needs no runtime here at all),
#     or ALLOW_PACKAGE_REMOVALS=1 to accept the removals once you have read the list it prints.
#   * It will not install or upgrade nginx when nginx is already present, because the upgrade
#     restarts it and every other site on the host blinks.
#   * It removes nginx's `default` site only when nothing else is being served here.

set -euo pipefail

HOSTNAME_ARG="${1:-uptime.example.com}"
SERVICE_USER="mtuptime"
APP_HOME="/opt/mt-uptime"
STATE_DIR="/var/lib/mt-uptime"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[[ $EUID -eq 0 ]] || { echo "must run as root (try: sudo $0 $HOSTNAME_ARG)" >&2; exit 1; }

# Refuse up front on anything that is not Debian/Ubuntu, rather than partway through.
#
# Every package operation below is apt-get, with no distro detection. On a dnf system this used to run
# far enough to create the service user and both directories, then die at the first `apt-get update`
# with "command not found" — leaving a half-provisioned host and an error that says nothing about why.
# README-deploy listed Amazon Linux 2023 as a supported target for a script that could never have
# worked there; that claim is gone, and this is the check that makes the limit explicit rather than
# implicit in a stack trace.
if ! command -v apt-get >/dev/null 2>&1; then
    {
        echo "REFUSING: this script provisions Debian/Ubuntu hosts and apt-get is not present here."
        echo
        echo "Nothing has been created or changed. On another distribution, follow sections 1-6 of"
        echo "README-deploy.md, which are the manual equivalent of what this script does — the only"
        echo "parts that differ are the package-manager commands."
    } >&2
    exit 1
fi

echo "==> service user"
id -u "$SERVICE_USER" >/dev/null 2>&1 \
    || useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"

echo "==> directories"
mkdir -p "$APP_HOME"
# systemd's StateDirectory= also creates this, but doing it here means a manual first run works too.
mkdir -p "$STATE_DIR"
chown -R "$SERVICE_USER:$SERVICE_USER" "$APP_HOME" "$STATE_DIR"
# The Data Protection keys live here. Group/other have no business reading them.
chmod 700 "$STATE_DIR"

# The environment file this script, the systemd unit and README-deploy all refer to — and which
# nothing created until now. README section 4 says to change the port here "not in the shipped unit",
# and the port-collision warning further down this very script says to set ASPNETCORE_URLS here; both
# were directing operators at a path that did not exist on a fresh install.
#
# Root-owned 0600 because this is where SendGrid keys and similar end up. Only the port is written:
# guessing App__PublicBaseUrl or AllowedHosts from $HOSTNAME_ARG would bake in the placeholder
# hostname for anyone who ran this script without one, and a wrong AllowedHosts rejects every real
# request with a 400. The example alongside it documents both, to be filled in deliberately.
mkdir -p /etc/mt-uptime
chmod 755 /etc/mt-uptime
install -m 0644 "$HERE/mt-uptime.env.example" /etc/mt-uptime/mt-uptime.env.example
if [[ ! -f /etc/mt-uptime/mt-uptime.env ]]; then
    printf '%s\n' \
        '# Written by provision.sh. See mt-uptime.env.example beside this file for everything else,' \
        '# in particular App__PublicBaseUrl and AllowedHosts once you have a real hostname.' \
        'ASPNETCORE_URLS=http://127.0.0.1:5081' \
        > /etc/mt-uptime/mt-uptime.env
    chmod 600 /etc/mt-uptime/mt-uptime.env
    echo "    wrote /etc/mt-uptime/mt-uptime.env (port only — set the rest deliberately)"
else
    echo "    /etc/mt-uptime/mt-uptime.env already exists — left alone"
fi

echo "==> ASP.NET Core runtime"
if [[ "${SKIP_RUNTIME:-}" == "1" ]]; then
    # A --self-contained build carries its own runtime, so there is nothing to install and no reason
    # to let apt near this host's packages at all.
    echo "    SKIP_RUNTIME=1 — leaving system packages alone; deploy a --self-contained build"
elif ! dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.AspNetCore.App 10\."; then
    apt-get update -qq

    # Simulate first. On Ubuntu 24.04 installing aspnetcore-runtime-10.0 REMOVES dotnet-host-8.0 —
    # i.e. /usr/bin/dotnet — and takes down every other .NET application on the box. That is not a
    # tradeoff a provisioning script gets to make on an operator's behalf, so: refuse, explain, and
    # point at the build that needs no runtime at all.
    REMOVALS="$(apt-get install -s -y aspnetcore-runtime-10.0 2>/dev/null | grep '^Remv' || true)"
    if [[ -n "$REMOVALS" ]]; then
        {
            echo
            echo "REFUSING: installing aspnetcore-runtime-10.0 would REMOVE packages that other"
            echo "applications on this host may depend on:"
            echo
            echo "$REMOVALS" | sed 's/^/    /'
            echo
            echo "No packages have been added or removed. Two ways forward:"
            echo
            echo "  1. Build self-contained on your machine — bundles the runtime, needs none here:"
            echo "       ./scripts/build-and-package.sh --self-contained"
            echo "     then re-run with the runtime step skipped, which touches no packages:"
            echo "       sudo SKIP_RUNTIME=1 $0 $HOSTNAME_ARG"
            echo
            echo "  2. If this host really is dedicated to MT-Uptime and the removals above are"
            echo "     genuinely unused, repeat with the removals accepted explicitly:"
            echo "       sudo ALLOW_PACKAGE_REMOVALS=1 $0 $HOSTNAME_ARG"
        } >&2
        [[ "${ALLOW_PACKAGE_REMOVALS:-}" == "1" ]] || exit 1
        echo "ALLOW_PACKAGE_REMOVALS=1 — proceeding with the removals above." >&2
    fi

    apt-get install -y -qq aspnetcore-runtime-10.0 \
        || { echo "Could not install aspnetcore-runtime-10.0 from the default repositories." >&2
             echo "See https://learn.microsoft.com/dotnet/core/install/linux for your distribution." >&2
             exit 1; }
fi
dotnet --list-runtimes 2>/dev/null | grep "Microsoft.AspNetCore.App 10\." || \
    echo "    no runtime installed — deploy a --self-contained build"

echo "==> nginx"
# Never install over an nginx that is already serving something. apt would pull a newer package if the
# index has one, and the upgrade restarts nginx — briefly dropping every site on the host. Skip it.
if command -v nginx >/dev/null 2>&1; then
    echo "    nginx already present ($(nginx -v 2>&1)) — NOT installing or upgrading it"
else
    apt-get install -y -qq nginx
fi
command -v curl >/dev/null 2>&1 || apt-get install -y -qq curl

echo "==> systemd unit"
cp "$HERE/mt-uptime.service" /etc/systemd/system/mt-uptime.service
systemctl daemon-reload
systemctl enable mt-uptime >/dev/null

echo "==> nginx site"
SITE=/etc/nginx/sites-available/mt-uptime
if [[ -f "$SITE" ]]; then
    echo "    $SITE already exists — leaving it alone"
else
    sed "s/uptime\.example\.com/$HOSTNAME_ARG/g" "$HERE/mt-uptime.nginx.conf.sample" > "$SITE"
    ln -sf "$SITE" /etc/nginx/sites-enabled/mt-uptime

    # Only on a box where nothing else is being served. On a shared host `default` may be a real site,
    # or the default_server that answers unmatched hostnames, and removing it changes behaviour for
    # every other name pointed here.
    OTHER_SITES="$(find /etc/nginx/sites-enabled -mindepth 1 ! -name mt-uptime ! -name default 2>/dev/null | wc -l)"
    if [[ "$OTHER_SITES" -eq 0 ]]; then
        rm -f /etc/nginx/sites-enabled/default
    elif [[ -e /etc/nginx/sites-enabled/default ]]; then
        echo "    other sites are enabled here — leaving the 'default' site in place"
    fi

    # A config that fails to parse is worse than no config: it sits there until someone reloads nginx
    # for an unrelated reason and takes their sites down with it. Undo and refuse.
    if ! nginx -t; then
        rm -f /etc/nginx/sites-enabled/mt-uptime "$SITE"
        echo "REFUSING: the generated nginx site does not parse (errors above); it has been removed." >&2
        echo "nginx was NOT reloaded and its running config is untouched." >&2
        exit 1
    fi
    systemctl reload nginx
fi

# The unit and the nginx site have to agree on a port, and the site was just written from the sample —
# so read the port back out of it rather than restating it here, and say so if something already holds
# it. Not fatal: nothing is deployed yet, and the fix is one line in /etc/mt-uptime/mt-uptime.env.
APP_PORT="$(grep -oP 'proxy_pass\s+https?://127\.0\.0\.1:\K[0-9]+' "$SITE" | head -1 || true)"
# "…unless the listener is us." Re-running this script on a host where MT-Uptime is already deployed
# and running otherwise warns that its own service is squatting the port, which is both false and
# exactly the kind of thing that teaches an operator to disregard the script's warnings.
if [[ -n "$APP_PORT" ]] && ss -ltn 2>/dev/null | grep -q ":$APP_PORT\b" \
   && ! systemctl is-active --quiet mt-uptime; then
    echo "    WARNING: something is already listening on port $APP_PORT." >&2
    echo "    MT-Uptime will fail to bind. Set ASPNETCORE_URLS in /etc/mt-uptime/mt-uptime.env to a" >&2
    echo "    free port and change proxy_pass in $SITE to match." >&2
fi

cat <<EOF

Provisioned. Next:

  1. Copy a build tarball up and deploy it:
       sudo ./deploy-on-server.sh mt-uptime.tar.gz

  2. Issue a certificate (DNS for $HOSTNAME_ARG must resolve here first):
       sudo apt-get install -y certbot python3-certbot-nginx
       sudo certbot --nginx -d $HOSTNAME_ARG

  3. Open https://$HOSTNAME_ARG/ and complete the setup wizard. It asks for a one-time token,
     generated when the app first starts with no accounts. Read it straight off disk:

       sudo cat $STATE_DIR/setup-token

     It is also printed to the log at startup. Note journald renders the whole message on one
     line, so pull the token out rather than reaching for grep -A:

       sudo journalctl -u mt-uptime | grep -oP 'one-time token:\s+\K[0-9a-f]+' | tail -1

     It stops whoever reaches the page first from claiming the instance, and is destroyed once
     your account exists.

Back up $STATE_DIR in full. It holds the database AND the Data Protection keys — without the keys,
every stored secret is permanently unreadable even with a perfect copy of the database.
EOF
