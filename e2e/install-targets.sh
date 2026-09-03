#!/usr/bin/env bash
#
# install-targets.sh — install and configure every service the E2E monitor tests probe.
#
#   sudo ./install-targets.sh [--with-ui] [--only <step>] [--no-selfcheck]
#
# Turns a fresh Ubuntu 24.04 box into a machine where all seven monitor types have something real to
# watch: an HTTP fixture behind nginx on plain HTTP and on four HTTPS ports with four different
# certificates, a TCP listener, a closed port, a blackholed port, an authoritative DNS zone, MySQL and
# PostgreSQL with TLS from our own CA, and a root-owned helper that can break and restore each of them.
#
# It does NOT install MT-Uptime. That is deploy/README-deploy.md's job, and doing it by hand is part of
# the test — every README command that misbehaves is a finding.
#
# Idempotent: safe to re-run, and the acceptance bar is that its self-check passes twice in a row.
# Every step converges rather than creates, prints why it skipped when it skips, and restarts a service
# only when that service's own configuration actually changed.
#
#   --with-ui        also install Chromium's shared-library dependencies, for the Playwright tier.
#                    The browser binaries themselves are installed per-user, later, by run-tests.sh.
#   --only <step>    run one step: certs, fixture, nginx, tcp, blackhole, dns, mysql, postgres,
#                    helper, ui, manifest. Skips the package install and, unless it is the named
#                    step, the manifest rewrite.
#   --no-selfcheck   skip the PASS/FAIL table at the end. For iterating on one step; never for a real
#                    run, because the table is the only thing that distinguishes "the script finished"
#                    from "the box is ready".
#
# ─────────────────────────────────────────────────────────────────────────────────────────────────
#  ORDER RELATIVE TO provision.sh AND deploy-on-server.sh
#
#  This script can run before or after them. That is a deliberate property, and the reason our nginx
#  configuration goes into /etc/nginx/conf.d/ rather than /etc/nginx/sites-enabled/ — see the comment
#  at the top of targets/nginx-e2e.conf, which explains how a file in sites-enabled would change
#  provision.sh's decision about Ubuntu's default site and cause the product's own /healthz to 404.
#
#  The one real coupling is the CA: the final step runs `systemctl try-restart mt-uptime` if that unit
#  exists, because a .NET process reads the system trust store on a cached basis and an instance that
#  started before our CA was installed may not see it. The restart is guarded, idempotent and cheap,
#  so it is done regardless of how long that cache actually lasts.
# ─────────────────────────────────────────────────────────────────────────────────────────────────

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGETS="$HERE/targets"

# --- the battery's fixed addresses ---------------------------------------------------------------
#
# Constants rather than flags. These are part of the contract between this script, the manifest, the
# nginx configuration and the tests; making them configurable would mean four places to keep in step
# for a benefit nobody has needed. 127.0.0.1 everywhere, never `localhost`, which on noble resolves
# ::1 first and would quietly test a different address than the one the services bind.

E2E_HOST=127.0.0.1
HTTP_PORT=8081
HTTPS_VALID_PORT=8443
HTTPS_EXPIRING_PORT=8444
HTTPS_EXPIRED_PORT=8445
HTTPS_UNTRUSTED_PORT=8446
FIXTURE_PORT=8090
TCP_PORT=8082
TCP_BLACKHOLE_PORT=8098
TCP_REFUSED_PORT=8099
MYSQL_PORT=3306
POSTGRES_PORT=5432

DNS_RESOLVER=127.0.0.2
DNS_ZONE=e2e.test
E2E_KEYWORD=MT-UPTIME-E2E-OK

ETC=/etc/mt-uptime-e2e
CERT_DIR="$ETC/certs"
MANIFEST="$ETC/targets.env"
OPT=/opt/mt-uptime-e2e
RUN_DIR=/run/mt-uptime-e2e

FIXTURE_UNIT=mt-uptime-e2e-fixture
TCP_UNIT=mt-uptime-e2e-tcp
BLACKHOLE_UNIT=mt-uptime-e2e-blackhole
DNS_UNIT=dnsmasq
MYSQL_UNIT=mysql

WITH_UI=0
ONLY=""
SELFCHECK=1

for arg in "$@"; do
    case "$arg" in
        --with-ui)      WITH_UI=1 ;;
        --only=*)       ONLY="${arg#--only=}" ;;
        --no-selfcheck) SELFCHECK=0 ;;
        -h|--help)      sed -n '2,32p' "$0"; exit 0 ;;
        *) echo "unknown option: $arg" >&2; echo "try: $0 --help" >&2; exit 1 ;;
    esac
done

# `--only <step>` as two words is what the help text promises and what anyone will type, but the loop
# above only understands --only=step. Rather than hand-roll a shift-based parser for one flag, accept
# both and say so.
if [[ -z "$ONLY" ]]; then
    prev=""
    for arg in "$@"; do
        [[ "$prev" == "--only" ]] && ONLY="$arg"
        prev="$arg"
    done
fi

[[ $EUID -eq 0 ]] || { echo "must run as root (try: sudo $0 $*)" >&2; exit 1; }

# Mirrors deploy/provision.sh: refuse up front on a distribution whose package manager we do not
# speak, rather than partway through with a half-configured box and a "command not found".
if ! command -v apt-get >/dev/null 2>&1; then
    {
        echo "REFUSING: this script configures Debian/Ubuntu hosts and apt-get is not present here."
        echo
        echo "Nothing has been created or changed. The battery targets Ubuntu 24.04; the package"
        echo "names, the AppArmor profile path, the PostgreSQL cluster layout and the systemd unit"
        echo "names below are all Debian-family specifics, so there is no near-miss to fall back on."
    } >&2
    exit 1
fi

export DEBIAN_FRONTEND=noninteractive
# Answer needrestart's "which services should be restarted?" dialogue automatically. Without it an
# apt-get install on noble can block forever on a full-screen prompt that a non-interactive session
# never sees.
export NEEDRESTART_MODE=a

echo "==> preflight"
# cloud-init is still installing packages and rewriting /etc/apt for the first minute or two of a
# fresh instance's life. Racing it produces lock contention at best and a half-written sources list at
# worst, and the failure looks like a broken mirror rather than a timing problem.
if command -v cloud-init >/dev/null 2>&1; then
    echo "    waiting for cloud-init to finish"
    cloud-init status --wait >/dev/null 2>&1 || true
fi

# --- state used across steps ----------------------------------------------------------------------

# Secrets are carried forward from an existing manifest rather than regenerated. A re-run that minted
# new database passwords would leave any test process holding the old manifest authenticating against
# a server that had just been ALTERed out from under it — and the resulting "Access denied" reads
# exactly like a checker defect.
if [[ -r "$MANIFEST" ]]; then
    # shellcheck source=/dev/null
    . "$MANIFEST"
fi

new_secret() { openssl rand -hex 16; }

HTTP_BASIC_USER="${HTTP_BASIC_USER:-e2e}"
HTTP_BASIC_PASS="${HTTP_BASIC_PASS:-$(new_secret)}"
HTTP_BEARER_TOKEN="${HTTP_BEARER_TOKEN:-$(new_secret)}"
MYSQL_PASSWORD="${MYSQL_PASSWORD:-$(new_secret)}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-$(new_secret)}"

# The account that will run `dotnet test`, and therefore the one the sudoers rule names. SUDO_USER is
# who invoked us; falling back to `ubuntu` covers the cloud image's default. A wrong guess is not
# fatal — the sudoers step warns and skips rather than writing a rule for an account that does not
# exist, which would be a rule nobody can use and a file nobody notices is wrong.
E2E_TEST_USER="${E2E_TEST_USER:-${SUDO_USER:-ubuntu}}"

HTTP_TOGGLE_FLAG="$RUN_DIR/http.down"
HTTP_SLOW_FLAG="$RUN_DIR/http.slow"

# Set by step_postgres, because the cluster version is discovered rather than assumed.
POSTGRES_VERSION="${POSTGRES_VERSION:-}"
POSTGRES_UNIT="${POSTGRES_UNIT:-}"

# install_file <src> <dest> <mode> — copy only when the content differs, and say which happened.
#
# The return value is the whole point: 0 means "changed", 1 means "already correct". Every caller uses
# it to decide whether to restart a service, which is what keeps a re-run from bouncing every daemon
# on the box.
install_file() {
    local src="$1" dest="$2" mode="${3:-0644}"
    if [[ -f "$dest" ]] && cmp -s "$src" "$dest"; then
        chmod "$mode" "$dest"
        return 1
    fi
    install -D -m "$mode" "$src" "$dest"
    return 0
}

# write_file <dest> <mode> — same contract, reading the content from stdin.
write_file() {
    local dest="$1" mode="${2:-0644}" tmp
    tmp="$(mktemp)"
    cat > "$tmp"
    if [[ -f "$dest" ]] && cmp -s "$tmp" "$dest"; then
        rm -f "$tmp"; chmod "$mode" "$dest"
        return 1
    fi
    install -D -m "$mode" "$tmp" "$dest"
    rm -f "$tmp"
    return 0
}

run_step() { [[ -z "$ONLY" || "$ONLY" == "$1" ]]; }

# ==================================================================================================
#  STEPS
# ==================================================================================================

step_packages() {
    echo "==> packages"

    # ─────────────────────────────────────────────────────────────────────────────────────────────
    #  dnsmasq's configuration MUST be on disk before the package is installed.
    #
    #  The Debian package starts dnsmasq from its own postinst. With no configuration of ours, that
    #  first start binds the wildcard address, collides with systemd-resolved on 127.0.0.53:53, and
    #  fails — during apt-get install, which then reports a package configuration error. Worse, the
    #  resolvconf hook may already have pointed /etc/resolv.conf at the instance that just died,
    #  leaving the box unable to resolve anything, including the mirror apt is mid-transaction with.
    #
    #  Writing both files first means the package's first start is already correct.
    # ─────────────────────────────────────────────────────────────────────────────────────────────
    install_file "$TARGETS/dnsmasq-e2e.conf" /etc/dnsmasq.d/mt-uptime-e2e.conf 0644 \
        && echo "    wrote /etc/dnsmasq.d/mt-uptime-e2e.conf (before installing dnsmasq, deliberately)" \
        || echo "    /etc/dnsmasq.d/mt-uptime-e2e.conf already current"

    write_file /etc/default/dnsmasq 0644 <<'EOF' \
        && echo "    wrote /etc/default/dnsmasq (resolvconf hook neutralised)" \
        || echo "    /etc/default/dnsmasq already current"
# Written by MT-Uptime's install-targets.sh.
#
# DNSMASQ_EXCEPT="lo" and IGNORE_RESOLVCONF=yes together stop the Debian package's resolvconf hook
# from pointing /etc/resolv.conf at our dnsmasq instance.
#
# That matters more than it looks. Our dnsmasq is configured `no-resolv` with no upstream servers,
# because it exists to be authoritative for one zone and to REFUSE everything else. Make it the
# system resolver and the box can no longer resolve anything at all — no apt, no NuGet, no
# dotnet-install.sh. The self-check asserts that archive.ubuntu.com still resolves, for exactly this
# reason.
DNSMASQ_EXCEPT="lo"
IGNORE_RESOLVCONF=yes
EOF

    apt-get update -qq

    # DPkg::Lock::Timeout rather than failing on contention: unattended-upgrades runs on a fresh
    # cloud image and holds the dpkg lock for a minute or two. Without this, the script dies with
    # "Could not get lock" on a box that is perfectly fine and would have been ready shortly.
    apt-get install -y -qq -o DPkg::Lock::Timeout=600 \
        nginx dnsmasq mysql-server postgresql nftables \
        openssl python3 sqlite3 curl jq \
        bind9-dnsutils mysql-client postgresql-client

    echo "    installed"
}

step_certs() {
    echo "==> certificates"
    "$TARGETS/make-certs.sh" --dir="$CERT_DIR"

    # The two database servers read their key as their own user, and neither failure mode is obvious:
    # PostgreSQL refuses to start at all, while mysqld starts with TLS SILENTLY OFF. Ownership is set
    # here rather than in make-certs.sh because that script has no business knowing which service
    # users exist — it is runnable on a developer machine with --no-trust and no databases at all.
    if id -u mysql >/dev/null 2>&1; then
        chown mysql:mysql "$CERT_DIR/mysql/server.key" "$CERT_DIR/mysql/server.crt"
        chmod 600 "$CERT_DIR/mysql/server.key"
    fi
    if id -u postgres >/dev/null 2>&1; then
        chown postgres:postgres "$CERT_DIR/postgres/server.key" "$CERT_DIR/postgres/server.crt"
        chmod 600 "$CERT_DIR/postgres/server.key"
    fi
}

step_fixture() {
    echo "==> HTTP fixture backend"
    install -d -m 0755 "$OPT"
    install -d -m 0755 "$RUN_DIR"

    local changed=0
    install_file "$TARGETS/fixture-server.py" "$OPT/fixture-server.py" 0755 && changed=1
    install_file "$TARGETS/systemd/$FIXTURE_UNIT.service" "/etc/systemd/system/$FIXTURE_UNIT.service" 0644 && changed=1

    write_file "$ETC/fixture.env" 0640 <<EOF && changed=1
# Written by install-targets.sh. Read by $FIXTURE_UNIT.service.
E2E_FIXTURE_HOST=$E2E_HOST
E2E_FIXTURE_PORT=$FIXTURE_PORT
E2E_KEYWORD=$E2E_KEYWORD
E2E_BASIC_USER=$HTTP_BASIC_USER
E2E_BASIC_PASS=$HTTP_BASIC_PASS
E2E_BEARER_TOKEN=$HTTP_BEARER_TOKEN
E2E_HTTP_TOGGLE_FLAG=$HTTP_TOGGLE_FLAG
E2E_HTTP_SLOW_FLAG=$HTTP_SLOW_FLAG
E2E_HTTP_SLOW_MS=1500
EOF

    systemctl daemon-reload
    systemctl enable "$FIXTURE_UNIT" >/dev/null 2>&1 || true
    if [[ $changed -eq 1 ]]; then
        echo "    configuration changed — restarting $FIXTURE_UNIT"
        systemctl restart "$FIXTURE_UNIT"
    else
        echo "    configuration unchanged — starting only if inactive"
        systemctl start "$FIXTURE_UNIT"
    fi

    # systemd reports "active" as soon as the process exists, which is before the socket accepts.
    # Polling the fixture's own /ok is the only statement worth making here.
    local i
    for ((i = 0; i < 60; i++)); do
        curl -sf -o /dev/null "http://$E2E_HOST:$FIXTURE_PORT/ok" && break
        sleep 0.5
    done
}

step_nginx() {
    echo "==> nginx"

    # conf.d/, not sites-enabled/. targets/nginx-e2e.conf explains why at length; the short version is
    # that a file in sites-enabled makes provision.sh keep Ubuntu's default_server, which then answers
    # the product's own loopback health check with a 404.
    local dest=/etc/nginx/conf.d/mt-uptime-e2e.conf

    if install_file "$TARGETS/nginx-e2e.conf" "$dest" 0644; then
        # Gate on nginx -t before reloading, exactly as provision.sh:167-174 does, and remove our file
        # on failure. A configuration that does not parse is worse than no configuration: it sits
        # there until somebody reloads nginx for an unrelated reason and takes every site on the box
        # down with it.
        if ! nginx -t; then
            rm -f "$dest"
            {
                echo "REFUSING: the E2E nginx configuration does not parse (errors above)."
                echo "It has been removed and nginx was NOT reloaded; its running config is untouched."
            } >&2
            exit 1
        fi
        systemctl reload nginx
        echo "    configuration installed and nginx reloaded"
    else
        echo "    /etc/nginx/conf.d/mt-uptime-e2e.conf already current — not reloading nginx"
    fi
}

step_tcp() {
    echo "==> TCP listener"
    install -d -m 0755 "$OPT"

    local changed=0
    install_file "$TARGETS/tcp-listener.py" "$OPT/tcp-listener.py" 0755 && changed=1
    install_file "$TARGETS/systemd/$TCP_UNIT.service" "/etc/systemd/system/$TCP_UNIT.service" 0644 && changed=1
    write_file "$ETC/tcp.env" 0644 <<EOF && changed=1
# Written by install-targets.sh. Read by $TCP_UNIT.service.
E2E_TCP_HOST=$E2E_HOST
E2E_TCP_PORT=$TCP_PORT
EOF

    systemctl daemon-reload
    systemctl enable "$TCP_UNIT" >/dev/null 2>&1 || true
    if [[ $changed -eq 1 ]]; then
        systemctl restart "$TCP_UNIT"; echo "    restarted $TCP_UNIT"
    else
        systemctl start "$TCP_UNIT"; echo "    $TCP_UNIT already current"
    fi

    # $TCP_REFUSED_PORT is left deliberately empty. Nothing to install; the note is here because an
    # unexplained gap in the port list invites somebody to fill it.
    echo "    port $TCP_REFUSED_PORT left closed on purpose (the connection-refused case)"
}

step_blackhole() {
    echo "==> TCP blackhole"
    local changed=0
    install_file "$TARGETS/systemd/$BLACKHOLE_UNIT.service" "/etc/systemd/system/$BLACKHOLE_UNIT.service" 0644 && changed=1
    write_file "$ETC/blackhole.env" 0644 <<EOF && changed=1
# Written by install-targets.sh. Read by $BLACKHOLE_UNIT.service.
E2E_TCP_BLACKHOLE_PORT=$TCP_BLACKHOLE_PORT
EOF

    systemctl daemon-reload
    systemctl enable "$BLACKHOLE_UNIT" >/dev/null 2>&1 || true
    # Always restarted, not conditionally: the unit is a oneshot whose effect is a kernel rule, and a
    # reboot or an `nft flush ruleset` from anything else on the box removes that rule while systemd
    # still considers the unit active. Re-running it is two nft calls and makes the state true again.
    systemctl restart "$BLACKHOLE_UNIT"
    echo "    dropping SYNs to port $TCP_BLACKHOLE_PORT via table inet mt_uptime_e2e"
}

step_dns() {
    echo "==> dnsmasq"
    # The configuration was written in step_packages, before the package existed. Re-assert it here so
    # that --only dns is a complete step, and restart only on a real change.
    if install_file "$TARGETS/dnsmasq-e2e.conf" /etc/dnsmasq.d/mt-uptime-e2e.conf 0644; then
        systemctl restart "$DNS_UNIT"; echo "    configuration changed — restarted $DNS_UNIT"
    else
        systemctl start "$DNS_UNIT" 2>/dev/null || systemctl restart "$DNS_UNIT"
        echo "    configuration unchanged"
    fi
    systemctl enable "$DNS_UNIT" >/dev/null 2>&1 || true
}

step_mysql() {
    echo "==> MySQL"

    # ─────────────────────────────────────────────────────────────────────────────────────────────
    #  AppArmor comes before the restart, and that ordering is the whole trap.
    #
    #  usr.sbin.mysqld is an enforcing profile on Ubuntu, and it does not grant read access to
    #  /etc/mt-uptime-e2e. Without a rule, mysqld cannot open the key — and rather than refusing to
    #  start, it comes up with TLS silently disabled. Every MySQL monitor then fails
    #  caching_sha2_password authentication, which looks exactly like a checker defect.
    #
    #  The local/ override file is the supported extension point; it is included by the shipped
    #  profile and survives package upgrades.
    # ─────────────────────────────────────────────────────────────────────────────────────────────
    local aa=/etc/apparmor.d/local/usr.sbin.mysqld
    if [[ -d /etc/apparmor.d/local ]]; then
        local aa_changed=0
        if ! grep -qF "$CERT_DIR/mysql/" "$aa" 2>/dev/null; then
            {
                echo "# Added by MT-Uptime install-targets.sh: mysqld must read the E2E TLS key, or it"
                echo "# starts with TLS silently OFF and every MySQL monitor fails to authenticate."
                echo "  $CERT_DIR/ r,"
                echo "  $CERT_DIR/** r,"
            } >> "$aa"
            aa_changed=1
        fi
        if [[ $aa_changed -eq 1 ]] && command -v apparmor_parser >/dev/null 2>&1; then
            apparmor_parser -r /etc/apparmor.d/usr.sbin.mysqld 2>/dev/null || true
            echo "    AppArmor local override added and profile reloaded"
        else
            echo "    AppArmor local override already present"
        fi
    fi

    local changed=0
    install_file "$TARGETS/mysql-e2e.cnf" /etc/mysql/mysql.conf.d/zz-mt-uptime-e2e.cnf 0644 && changed=1

    if [[ $changed -eq 1 ]]; then
        systemctl restart "$MYSQL_UNIT"; echo "    configuration changed — restarted $MYSQL_UNIT"
    else
        systemctl start "$MYSQL_UNIT"
        # A reload is enough to pick up rotated certificates when the cnf itself has not moved, and it
        # does not drop the connections of anything else using this server.
        mysql --protocol=socket -e "ALTER INSTANCE RELOAD TLS" 2>/dev/null \
            && echo "    configuration unchanged — reloaded TLS in place" \
            || echo "    configuration unchanged"
    fi
    systemctl enable "$MYSQL_UNIT" >/dev/null 2>&1 || true

    # Provision over the root unix socket, from a temp file. Not `mysql -e "...$PASSWORD..."`: that
    # puts the password in the process table for anything on the box to read, which on a shared host
    # is a disclosure and on any host is a bad habit to encode in a script others copy.
    local sql; sql="$(mktemp)"
    chmod 600 "$sql"
    sed "s|__E2E_MYSQL_PASSWORD__|$MYSQL_PASSWORD|g" "$TARGETS/mysql-init.sql" > "$sql"
    mysql --protocol=socket < "$sql"
    rm -f "$sql"
    echo "    database 'e2e' and role 'e2e_probe' provisioned"
}

step_postgres() {
    echo "==> PostgreSQL"

    # Discovered, not assumed: noble ships 16 today and the next LTS will not. The per-cluster unit is
    # what must be reloaded — postgresql.service is an umbrella target that can report success without
    # the cluster having reloaded anything.
    if [[ ! -d /etc/postgresql ]]; then
        echo "    WARNING: /etc/postgresql does not exist — is postgresql installed?" >&2
        return 0
    fi
    POSTGRES_VERSION="$(ls /etc/postgresql | sort -V | tail -1)"
    POSTGRES_UNIT="postgresql@${POSTGRES_VERSION}-main"
    echo "    cluster $POSTGRES_VERSION, unit $POSTGRES_UNIT"

    local conf="/etc/postgresql/$POSTGRES_VERSION/main/conf.d/mt-uptime-e2e.conf"
    local changed=0
    install_file "$TARGETS/postgres-e2e.conf" "$conf" 0644 && changed=1

    # The stock pg_hba.conf on Debian already has
    #     host all all 127.0.0.1/32 scram-sha-256
    # which is exactly what the tests need. Asserted rather than appended: rewriting an operator's
    # authentication file to add a line that is already there is how a working cluster stops working.
    local hba="/etc/postgresql/$POSTGRES_VERSION/main/pg_hba.conf"
    if grep -qE '^[[:space:]]*host[[:space:]]+all[[:space:]]+all[[:space:]]+127\.0\.0\.1/32[[:space:]]+scram-sha-256' "$hba"; then
        echo "    pg_hba.conf already permits scram-sha-256 from 127.0.0.1"
    else
        echo "    WARNING: pg_hba.conf has no scram-sha-256 rule for 127.0.0.1/32." >&2
        echo "    The database monitors will fail to authenticate. Add:" >&2
        echo "        host all all 127.0.0.1/32 scram-sha-256" >&2
    fi

    if [[ $changed -eq 1 ]]; then
        systemctl restart "$POSTGRES_UNIT"; echo "    configuration changed — restarted $POSTGRES_UNIT"
    else
        systemctl start "$POSTGRES_UNIT"
        systemctl reload "$POSTGRES_UNIT" 2>/dev/null || true
        echo "    configuration unchanged — reloaded"
    fi
    systemctl enable "$POSTGRES_UNIT" >/dev/null 2>&1 || true

    # Wait for the cluster to accept connections before provisioning. `systemctl start` returns once
    # postgres has forked, which is before recovery finishes on a cluster that was not shut down
    # cleanly, and `psql` would then fail with "the database system is starting up".
    local i
    for ((i = 0; i < 60; i++)); do
        runuser -u postgres -- pg_isready -q -h "$E2E_HOST" -p "$POSTGRES_PORT" && break
        sleep 0.5
    done

    # `cd /` via runuser's own working directory: psql emits a "could not change directory to /root"
    # warning otherwise, which looks like a failure and is not.
    local sql; sql="$(mktemp)"
    chmod 600 "$sql"
    sed "s|__E2E_POSTGRES_PASSWORD__|$POSTGRES_PASSWORD|g" "$TARGETS/postgres-init.sql" > "$sql"
    chown postgres "$sql"

    # The database is created here rather than in the SQL file because CREATE DATABASE cannot run
    # inside a transaction block, and psql -f wraps the file in one.
    if ! runuser -u postgres -- psql -tAc "SELECT 1 FROM pg_database WHERE datname='e2e'" 2>/dev/null | grep -q 1; then
        runuser -u postgres -- createdb -O postgres e2e
        echo "    created database 'e2e'"
    fi
    ( cd / && runuser -u postgres -- psql -q -v ON_ERROR_STOP=1 -f "$sql" )
    rm -f "$sql"
    echo "    role 'e2e_probe' provisioned"
}

step_helper() {
    echo "==> break/restore helper"
    install_file "$TARGETS/mt-uptime-e2e-target" /usr/local/bin/mt-uptime-e2e-target 0755 \
        && echo "    installed /usr/local/bin/mt-uptime-e2e-target (0755 root:root)" \
        || echo "    /usr/local/bin/mt-uptime-e2e-target already current"
    # Explicit, because install(1) preserves the invoking user's ownership only by coincidence of
    # running as root, and this file being root-owned is what keeps the sudoers rule narrow.
    chown root:root /usr/local/bin/mt-uptime-e2e-target

    if ! id -u "$E2E_TEST_USER" >/dev/null 2>&1; then
        echo "    WARNING: user '$E2E_TEST_USER' does not exist — sudoers rule NOT installed." >&2
        echo "    The tests will fail at the first break/restore. Re-run with" >&2
        echo "        sudo E2E_TEST_USER=<account> $0" >&2
        return 0
    fi

    local tmp; tmp="$(mktemp)"
    sed "s|__E2E_TEST_USER__|$E2E_TEST_USER|g" "$TARGETS/mt-uptime-e2e.sudoers" > "$tmp"

    # visudo -cf before installing. A malformed file in /etc/sudoers.d does not merely fail to grant
    # what it meant to — it can make sudo refuse to run at all, and recovering from that on a cloud
    # instance with no root password means a rescue boot.
    if ! visudo -cf "$tmp" >/dev/null; then
        rm -f "$tmp"
        echo "REFUSING: the generated sudoers file does not validate. Nothing was installed." >&2
        exit 1
    fi

    # No dot in the installed name: sudo silently ignores any file in sudoers.d whose name contains
    # one, so mt-uptime-e2e.sudoers would validate, install, and do nothing.
    if [[ -f /etc/sudoers.d/mt-uptime-e2e ]] && cmp -s "$tmp" /etc/sudoers.d/mt-uptime-e2e; then
        echo "    /etc/sudoers.d/mt-uptime-e2e already current"
    else
        install -m 0440 -o root -g root "$tmp" /etc/sudoers.d/mt-uptime-e2e
        echo "    installed /etc/sudoers.d/mt-uptime-e2e for '$E2E_TEST_USER'"
    fi
    rm -f "$tmp"
}

step_ui() {
    echo "==> Chromium OS dependencies"
    # noble renamed a batch of these with a t64 suffix during the 64-bit time_t transition, so the
    # list that works on jammy fails here with "Unable to locate package". Verify against
    # `playwright install-deps --dry-run` on the box if Playwright ever complains about a missing
    # shared library.
    #
    # The browser binaries themselves are NOT installed here: Playwright keeps them per-user under
    # ~/.cache/ms-playwright, and installing them as root would put them somewhere the test user
    # cannot use. run-tests.sh --tier ui does that step.
    apt-get install -y -qq -o DPkg::Lock::Timeout=600 \
        libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 \
        libcairo2 libcups2t64 libdbus-1-3 libdrm2 libgbm1 libglib2.0-0t64 \
        libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 \
        libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 \
        fonts-liberation fonts-noto-color-emoji
    echo "    installed (browser binaries come later, per user, from run-tests.sh --tier ui)"
}

write_manifest() {
    echo "==> manifest"

    # Rewritten in full every run rather than patched, so it can never carry a stale key from an
    # earlier layout. Values are unquoted KEY=VALUE, which both `source` and a five-line C# parser
    # read the same way — the tests use the latter, this script and the helper use the former, and a
    # quoting convention that only one of them understood would be a silent divergence.
    local notafter="$CERT_DIR/notafter.env"
    local tls_valid="" tls_expiring="" tls_expired="" tls_untrusted=""
    if [[ -r "$notafter" ]]; then
        # shellcheck source=/dev/null
        . "$notafter"
        tls_valid="$TLS_VALID_NOT_AFTER"
        tls_expiring="$TLS_EXPIRING_NOT_AFTER"
        tls_expired="$TLS_EXPIRED_NOT_AFTER"
        tls_untrusted="$TLS_UNTRUSTED_NOT_AFTER"
    fi

    install -d -m 0755 "$ETC"
    write_file "$MANIFEST" 0640 <<EOF >/dev/null || true
# MT-Uptime E2E target manifest. Written by install-targets.sh; do not edit by hand — it is rewritten
# in full on every run. Read by e2e/*.sh (via source) and by Tests.E2E.MT-Uptime (Support/Targets.cs).
E2E_MANIFEST_VERSION=1
E2E_GENERATED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)
E2E_HOST=$E2E_HOST
E2E_HOSTNAME=localhost
E2E_KEYWORD=$E2E_KEYWORD
E2E_HELPER=/usr/local/bin/mt-uptime-e2e-target
E2E_TEST_USER=$E2E_TEST_USER
UI_DEPS_INSTALLED=$WITH_UI

HTTP_PORT=$HTTP_PORT
HTTP_BASE_URL=http://$E2E_HOST:$HTTP_PORT
HTTPS_VALID_PORT=$HTTPS_VALID_PORT
HTTPS_EXPIRING_PORT=$HTTPS_EXPIRING_PORT
HTTPS_EXPIRED_PORT=$HTTPS_EXPIRED_PORT
HTTPS_UNTRUSTED_PORT=$HTTPS_UNTRUSTED_PORT
FIXTURE_PORT=$FIXTURE_PORT
HTTP_BASIC_USER=$HTTP_BASIC_USER
HTTP_BASIC_PASS=$HTTP_BASIC_PASS
HTTP_BEARER_TOKEN=$HTTP_BEARER_TOKEN
HTTP_TOGGLE_FLAG=$HTTP_TOGGLE_FLAG
HTTP_SLOW_FLAG=$HTTP_SLOW_FLAG

TCP_PORT=$TCP_PORT
TCP_BLACKHOLE_PORT=$TCP_BLACKHOLE_PORT
TCP_REFUSED_PORT=$TCP_REFUSED_PORT

DNS_RESOLVER=$DNS_RESOLVER
DNS_ZONE=$DNS_ZONE
DNS_A_NAME=a.$DNS_ZONE
DNS_A_VALUE=10.10.10.10
DNS_AAAA_VALUE=fd00:e2e::10
DNS_CNAME_NAME=cname.$DNS_ZONE
DNS_CNAME_VALUE=a.$DNS_ZONE.
DNS_MX_NAME=$DNS_ZONE
DNS_MX_VALUE=mail.$DNS_ZONE.
DNS_MX_PREFERENCE=10
DNS_TXT_NAME=txt.$DNS_ZONE
DNS_TXT_VALUE=mt-uptime-e2e
DNS_NXDOMAIN_NAME=missing.$DNS_ZONE

MYSQL_HOST=$E2E_HOST
MYSQL_PORT=$MYSQL_PORT
MYSQL_DATABASE=e2e
MYSQL_USER=e2e_probe
MYSQL_PASSWORD=$MYSQL_PASSWORD
MYSQL_UNIT=$MYSQL_UNIT

POSTGRES_HOST=$E2E_HOST
POSTGRES_PORT=$POSTGRES_PORT
POSTGRES_DATABASE=e2e
POSTGRES_USER=e2e_probe
POSTGRES_PASSWORD=$POSTGRES_PASSWORD
POSTGRES_VERSION=$POSTGRES_VERSION
POSTGRES_UNIT=$POSTGRES_UNIT

CERT_DIR=$CERT_DIR
CA_CERT=$CERT_DIR/ca/ca.crt
CA_SYSTEM_PATH=/usr/local/share/ca-certificates/mt-uptime-e2e-ca.crt
CA_UNTRUSTED_CERT=$CERT_DIR/ca-untrusted/ca.crt
TLS_VALID_NOT_AFTER=$tls_valid
TLS_EXPIRING_NOT_AFTER=$tls_expiring
TLS_EXPIRING_DAYS=5
TLS_EXPIRED_NOT_AFTER=$tls_expired
TLS_UNTRUSTED_NOT_AFTER=$tls_untrusted

FIXTURE_UNIT=$FIXTURE_UNIT
TCP_UNIT=$TCP_UNIT
DNS_UNIT=$DNS_UNIT
BLACKHOLE_UNIT=$BLACKHOLE_UNIT
EOF

    # 0640 root:<test user>, so the test process can read the database passwords it needs while no
    # other local account can.
    if id -u "$E2E_TEST_USER" >/dev/null 2>&1; then
        chown "root:$(id -gn "$E2E_TEST_USER")" "$MANIFEST"
    fi
    echo "    wrote $MANIFEST"
}

restart_app_if_present() {
    # The CA went into /etc/ssl/certs during step_certs. A .NET process reads the system trust store
    # on a cached basis, so an MT-Uptime that started earlier may not see it — and the symptom would
    # be every VerifyCa database monitor and every HTTPS monitor reporting a certificate error, which
    # is indistinguishable from a checker defect.
    #
    # try-restart rather than restart: it is a no-op when the unit is not running, and this script has
    # to work on a box where MT-Uptime has not been deployed yet.
    if systemctl list-unit-files mt-uptime.service >/dev/null 2>&1 \
       && systemctl is-active --quiet mt-uptime 2>/dev/null; then
        echo "==> restarting mt-uptime so it picks up the new CA"
        systemctl try-restart mt-uptime
    fi
}

# ==================================================================================================
#  SELF-CHECK
# ==================================================================================================

CHECK_PASS=0
CHECK_FAIL=0
CHECK_WARN=0
CHECK_ROWS=()

# check <name> <command...>
#
# The status is captured through `if`, never as `out=$(...)` followed by `$?`.
#
# That is the lesson deploy-on-server.sh records at length: under `set -euo pipefail`, a non-zero exit
# inside a command substitution — grep finding nothing, a missing file — propagates out and kills the
# script. A self-check that dies on its first failing check instead of reporting it is worse than no
# self-check, because the output looks like a crash rather than a result.
check() {
    local name="$1"; shift
    local out status
    if out="$("$@" 2>&1)"; then status=0; else status=$?; fi
    if [[ $status -eq 0 ]]; then
        CHECK_PASS=$((CHECK_PASS + 1))
        CHECK_ROWS+=("PASS|$name|")
    else
        CHECK_FAIL=$((CHECK_FAIL + 1))
        CHECK_ROWS+=("FAIL|$name|$(printf '%s' "$out" | tr '\n' ' ' | cut -c1-140)")
    fi
}

# warn <name> <command...> — reported, never fatal.
warn() {
    local name="$1"; shift
    local out status
    if out="$("$@" 2>&1)"; then status=0; else status=$?; fi
    if [[ $status -eq 0 ]]; then
        CHECK_PASS=$((CHECK_PASS + 1))
        CHECK_ROWS+=("PASS|$name|")
    else
        CHECK_WARN=$((CHECK_WARN + 1))
        CHECK_ROWS+=("WARN|$name|$(printf '%s' "$out" | tr '\n' ' ' | cut -c1-140)")
    fi
}

# --- predicates the checks call -------------------------------------------------------------------
#
# Functions rather than inline pipelines, so `check` receives a command whose status means one thing.

http_status_is() {  # <expected> <url> [curl args...]
    local want="$1" url="$2"; shift 2
    local got; got="$(curl -s -o /dev/null -m 10 -w '%{http_code}' "$@" "$url")"
    [[ "$got" == "$want" ]] || { echo "expected $want, got $got, for $url"; return 1; }
}

body_contains() {  # <needle> <url> [curl args...]
    local needle="$1" url="$2"; shift 2
    local body; body="$(curl -s -m 10 "$@" "$url")"
    [[ "$body" == *"$needle"* ]] || { echo "'$needle' not in body of $url"; return 1; }
}

header_is() {  # <header> <expected> <url>
    local name="$1" want="$2" url="$3" got
    got="$(curl -sI -m 10 "$url" | tr -d '\r' | awk -F': ' -v h="$name" 'tolower($1)==tolower(h){print $2}' | head -1)"
    [[ "$got" == "$want" ]] || { echo "$name was '$got', expected '$want'"; return 1; }
}

echo_roundtrip() {
    local body; body="$(curl -s -m 10 -X POST -H 'X-E2E-Probe: roundtrip' -d 'e2e-body' \
        "http://$E2E_HOST:$HTTP_PORT/echo")"
    [[ "$body" == *"x-e2e-probe"* && "$body" == *"roundtrip"* && "$body" == *"e2e-body"* ]] \
        || { echo "echo did not reflect the header and body: $body"; return 1; }
}

port_connects() {  # <host> <port>
    timeout 5 bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null \
        || { echo "cannot connect to $1:$2"; return 1; }
}

port_refuses_fast() {  # <host> <port> — non-zero, but NOT the timeout exit
    local rc=0
    timeout 5 bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null || rc=$?
    [[ $rc -ne 0 ]] || { echo "$1:$2 accepted a connection; it should be closed"; return 1; }
    [[ $rc -ne 124 ]] || { echo "$1:$2 timed out; a closed port should refuse immediately"; return 1; }
}

port_blackholed() {  # <host> <port> — must time out, i.e. exit 124
    local rc=0
    timeout 3 bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null || rc=$?
    [[ $rc -eq 124 ]] \
        || { echo "$1:$2 exited $rc, expected 124 (timeout). The nft drop rule is not in effect."; return 1; }
}

tls_serves() {  # <port> — trusted by our CA, 200 through the proxy
    local port="$1" got
    got="$(curl -s -o /dev/null -m 10 --cacert "$CERT_DIR/ca/ca.crt" \
        --resolve "localhost:$port:$E2E_HOST" -w '%{http_code}' "https://localhost:$port/ok")"
    [[ "$got" == "200" ]] || { echo "port $port returned $got with our CA"; return 1; }
}

tls_rejected_without_ca() {  # <port> — curl exit 60, "cert verify failed"
    local port="$1" rc=0
    curl -s -o /dev/null -m 10 --resolve "localhost:$port:$E2E_HOST" "https://localhost:$port/ok" 2>/dev/null || rc=$?
    [[ $rc -eq 60 ]] || { echo "port $port gave curl exit $rc, expected 60"; return 1; }
}

tls_ok_when_insecure() {  # <port> — -k gets a 200 even from an expired/untrusted cert
    local got; got="$(curl -sk -o /dev/null -m 10 -w '%{http_code}' "https://$E2E_HOST:$1/ok")"
    [[ "$got" == "200" ]] || { echo "port $1 returned $got with -k"; return 1; }
}

cert_checkend() {  # <port> <seconds> <expect-valid|expect-expiring>
    local port="$1" secs="$2" expect="$3" pem
    pem="$(echo | timeout 10 openssl s_client -connect "$E2E_HOST:$port" -servername localhost 2>/dev/null \
        | openssl x509 2>/dev/null)"
    [[ -n "$pem" ]] || { echo "no certificate read from port $port"; return 1; }
    if printf '%s' "$pem" | openssl x509 -noout -checkend "$secs" >/dev/null 2>&1; then
        [[ "$expect" == "expect-valid" ]] || { echo "port $port is still valid beyond ${secs}s"; return 1; }
    else
        [[ "$expect" == "expect-expiring" ]] || { echo "port $port expires within ${secs}s"; return 1; }
    fi
}

untrusted_issuer_is_marked() {
    local issuer
    issuer="$(echo | timeout 10 openssl s_client -connect "$E2E_HOST:$HTTPS_UNTRUSTED_PORT" 2>/dev/null \
        | openssl x509 -noout -issuer 2>/dev/null)"
    [[ "$issuer" == *UNTRUSTED* ]] \
        || { echo "issuer on port $HTTPS_UNTRUSTED_PORT is '$issuer' — the two CAs may be swapped"; return 1; }
}

dig_contains() {  # <name> <type> <needle>
    local out
    # Exit status AND content: `dig +short` writes "communications error ..." to STDOUT, so a
    # non-empty-output test reports a dead resolver as healthy. Measured, not theorised.
    out="$(dig "@$DNS_RESOLVER" +short +time=2 +tries=1 "$1" "$2" 2>/dev/null)" \
        || { echo "dig failed for $1 $2"; return 1; }
    [[ "$out" == *"$3"* ]] || { echo "$1 $2 returned '$out', expected to contain '$3'"; return 1; }
}

dig_nxdomain() {
    local out
    out="$(dig "@$DNS_RESOLVER" +time=2 +tries=1 "$DNS_NXDOMAIN_NAME" A 2>/dev/null)" \
        || { echo "dig failed"; return 1; }
    [[ "$out" == *"NXDOMAIN"* ]] || { echo "expected NXDOMAIN for $DNS_NXDOMAIN_NAME"; return 1; }
}

system_resolver_intact() {
    # The single most valuable check here. If dnsmasq has hijacked /etc/resolv.conf, this box can no
    # longer reach apt, NuGet or the .NET installer, and every later failure will be blamed on
    # something else.
    getent hosts archive.ubuntu.com >/dev/null 2>&1 \
        || { echo "archive.ubuntu.com does not resolve — dnsmasq has taken over the system resolver"; return 1; }
}

mysql_probe() {  # <extra mysql args...>
    local cnf; cnf="$(mktemp)"; chmod 600 "$cnf"
    printf '[client]\nuser=%s\npassword=%s\nhost=%s\nport=%s\n' \
        "e2e_probe" "$MYSQL_PASSWORD" "$E2E_HOST" "$MYSQL_PORT" > "$cnf"
    local out status
    if out="$(mysql --defaults-extra-file="$cnf" "$@" -N -B -e 'SELECT 1' 2>&1)"; then status=0; else status=$?; fi
    rm -f "$cnf"
    [[ $status -eq 0 && "$out" == *1* ]] || { echo "$out"; return 1; }
}

mysql_tls_actually_on() {
    local cnf; cnf="$(mktemp)"; chmod 600 "$cnf"
    printf '[client]\nuser=%s\npassword=%s\nhost=%s\nport=%s\n' \
        "e2e_probe" "$MYSQL_PASSWORD" "$E2E_HOST" "$MYSQL_PORT" > "$cnf"
    local out
    out="$(mysql --defaults-extra-file="$cnf" --ssl-mode=REQUIRED -N -B \
        -e "SHOW STATUS LIKE 'Ssl_cipher'" 2>&1 || true)"
    rm -f "$cnf"
    # Asserted on the cipher rather than on "the connection worked", because mysqld starts happily
    # with TLS off when it cannot read its key — and then a plain login still succeeds while every
    # VerifyCa monitor fails.
    [[ "$out" == *Ssl_cipher* && "$out" != *$'Ssl_cipher\t\n'* && -n "${out//Ssl_cipher/}" ]] \
        || { echo "Ssl_cipher is empty — mysqld is running WITHOUT TLS: $out"; return 1; }
}

pg_probe() {  # <sslmode> [extra libpq params]
    local out
    out="$(PGPASSWORD="$POSTGRES_PASSWORD" psql \
        "host=$E2E_HOST port=$POSTGRES_PORT dbname=e2e user=e2e_probe $*" \
        -tAc 'SELECT 1' 2>&1)" || { echo "$out"; return 1; }
    [[ "$out" == *1* ]] || { echo "$out"; return 1; }
}

pg_tls_actually_on() {
    local out
    out="$(PGPASSWORD="$POSTGRES_PASSWORD" psql \
        "host=$E2E_HOST port=$POSTGRES_PORT dbname=e2e user=e2e_probe sslmode=require" \
        -tAc 'SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()' 2>&1)" || { echo "$out"; return 1; }
    [[ "$out" == *t* ]] || { echo "pg_stat_ssl.ssl is '$out', expected t"; return 1; }
}

helper_roundtrip_as_test_user() {
    # The check that proves the sudoers rule, the helper, the flag file and the fixture all agree —
    # end to end, as the account that will actually run the tests. Each of those four has its own
    # silent failure mode, and this is the only assertion that covers the seam between them.
    local h=/usr/local/bin/mt-uptime-e2e-target
    runuser -u "$E2E_TEST_USER" -- sudo -n "$h" break http >/dev/null 2>&1 \
        || { echo "'sudo -n $h break http' failed as $E2E_TEST_USER"; return 1; }
    local down; down="$(curl -s -o /dev/null -m 10 -w '%{http_code}' "http://$E2E_HOST:$HTTP_PORT/toggle")"
    runuser -u "$E2E_TEST_USER" -- sudo -n "$h" restore http >/dev/null 2>&1 \
        || { echo "restore failed; the fixture may still be broken"; return 1; }
    local up; up="$(curl -s -o /dev/null -m 10 -w '%{http_code}' "http://$E2E_HOST:$HTTP_PORT/toggle")"
    [[ "$down" == "503" && "$up" == "200" ]] \
        || { echo "break gave $down (want 503), restore gave $up (want 200)"; return 1; }
}

unit_active()  { systemctl is-active --quiet "$1" || { echo "$1 is not active"; return 1; }; }
unit_enabled() { systemctl is-enabled --quiet "$1" || { echo "$1 is not enabled"; return 1; }; }

nft_rule_present() {
    local out
    out="$(nft list table inet mt_uptime_e2e 2>&1)" || { echo "$out"; return 1; }
    [[ "$out" == *"dport $TCP_BLACKHOLE_PORT"* && "$out" == *drop* ]] \
        || { echo "table exists but has no drop rule for $TCP_BLACKHOLE_PORT"; return 1; }
}

clock_synchronised() {
    # A WARN, not a FAIL. Every certificate assertion is day arithmetic against the local clock, so a
    # box whose time is wrong produces cert failures that look like minting bugs. Not fatal, because a
    # freshly-booted instance can take a minute to sync and the certificates have a day of slack.
    local out
    out="$(timedatectl show -p NTPSynchronized --value 2>/dev/null)" || { echo "timedatectl unavailable"; return 1; }
    [[ "$out" == "yes" ]] || { echo "clock is not NTP-synchronised; certificate day arithmetic may be off"; return 1; }
}

manifest_parses() {
    # Round-tripped through python as a stand-in for the five-line C# parser in Support/Targets.cs, so
    # a quoting change here fails the installer rather than the test suite.
    python3 - "$MANIFEST" <<'PY'
import sys
required = ["E2E_HOST", "HTTP_BASE_URL", "TCP_PORT", "DNS_RESOLVER",
            "MYSQL_PASSWORD", "POSTGRES_PASSWORD", "CERT_DIR", "TLS_EXPIRING_NOT_AFTER"]
seen = {}
for line in open(sys.argv[1]):
    line = line.strip()
    if not line or line.startswith("#") or "=" not in line:
        continue
    k, v = line.split("=", 1)
    seen[k] = v
missing = [k for k in required if not seen.get(k)]
if missing:
    print("missing or empty keys:", ", ".join(missing))
    sys.exit(1)
PY
}

self_check() {
    echo
    echo "==> self-check"

    check "fixture /ok carries the keyword"        body_contains "$E2E_KEYWORD" "http://$E2E_HOST:$HTTP_PORT/ok"
    check "fixture /status/503 returns 503"        http_status_is 503 "http://$E2E_HOST:$HTTP_PORT/status/503"
    check "fixture /status/204 returns 204"        http_status_is 204 "http://$E2E_HOST:$HTTP_PORT/status/204"
    check "fixture /basic refuses anonymously"     http_status_is 401 "http://$E2E_HOST:$HTTP_PORT/basic"
    check "fixture /basic accepts the credentials" http_status_is 200 "http://$E2E_HOST:$HTTP_PORT/basic" -u "$HTTP_BASIC_USER:$HTTP_BASIC_PASS"
    check "fixture /bearer accepts the token"      http_status_is 200 "http://$E2E_HOST:$HTTP_PORT/bearer" -H "Authorization: Bearer $HTTP_BEARER_TOKEN"
    check "fixture /bearer refuses a wrong token"  http_status_is 401 "http://$E2E_HOST:$HTTP_PORT/bearer" -H "Authorization: Bearer wrong"
    check "fixture /redirect is a 302 to /ok"      header_is Location /ok "http://$E2E_HOST:$HTTP_PORT/redirect"
    check "fixture /echo reflects header and body" echo_roundtrip
    check "fixture /toggle is healthy"             http_status_is 200 "http://$E2E_HOST:$HTTP_PORT/toggle"
    check "nginx serves the fixture on $HTTP_PORT" http_status_is 200 "http://$E2E_HOST:$HTTP_PORT/ok"

    check "break/restore works as $E2E_TEST_USER"  helper_roundtrip_as_test_user

    check "TLS $HTTPS_VALID_PORT trusted by our CA"      tls_serves "$HTTPS_VALID_PORT"
    check "TLS $HTTPS_EXPIRING_PORT trusted by our CA"   tls_serves "$HTTPS_EXPIRING_PORT"
    check "TLS $HTTPS_EXPIRED_PORT rejected (exit 60)"   tls_rejected_without_ca "$HTTPS_EXPIRED_PORT"
    check "TLS $HTTPS_UNTRUSTED_PORT rejected (exit 60)" tls_rejected_without_ca "$HTTPS_UNTRUSTED_PORT"
    check "TLS $HTTPS_EXPIRED_PORT serves with -k"       tls_ok_when_insecure "$HTTPS_EXPIRED_PORT"
    check "TLS $HTTPS_UNTRUSTED_PORT serves with -k"     tls_ok_when_insecure "$HTTPS_UNTRUSTED_PORT"
    check "cert $HTTPS_VALID_PORT valid past 300d"       cert_checkend "$HTTPS_VALID_PORT" 25920000 expect-valid
    check "cert $HTTPS_EXPIRING_PORT expires inside 14d" cert_checkend "$HTTPS_EXPIRING_PORT" 1209600 expect-expiring
    check "cert $HTTPS_EXPIRING_PORT valid past 4d"      cert_checkend "$HTTPS_EXPIRING_PORT" 345600 expect-valid
    check "cert $HTTPS_EXPIRED_PORT has expired"         cert_checkend "$HTTPS_EXPIRED_PORT" 0 expect-expiring
    check "untrusted issuer is marked UNTRUSTED"         untrusted_issuer_is_marked

    check "TCP $TCP_PORT accepts"                  port_connects "$E2E_HOST" "$TCP_PORT"
    check "TCP $TCP_REFUSED_PORT refuses fast"     port_refuses_fast "$E2E_HOST" "$TCP_REFUSED_PORT"
    check "TCP $TCP_BLACKHOLE_PORT times out"      port_blackholed "$E2E_HOST" "$TCP_BLACKHOLE_PORT"
    check "nft drop rule is in effect"             nft_rule_present

    check "DNS A record"                           dig_contains "a.$DNS_ZONE" A 10.10.10.10
    check "DNS AAAA record"                        dig_contains "a.$DNS_ZONE" AAAA "fd00:e2e::10"
    check "DNS CNAME record"                       dig_contains "cname.$DNS_ZONE" CNAME "a.$DNS_ZONE."
    check "DNS MX record"                          dig_contains "$DNS_ZONE" MX "mail.$DNS_ZONE."
    check "DNS TXT record"                         dig_contains "txt.$DNS_ZONE" TXT mt-uptime-e2e
    check "DNS NXDOMAIN for a missing name"        dig_nxdomain
    check "the system resolver still works"        system_resolver_intact

    check "MySQL without TLS"                      mysql_probe --ssl-mode=DISABLED --get-server-public-key
    check "MySQL with VERIFY_CA"                   mysql_probe --ssl-mode=VERIFY_CA --ssl-ca="$CERT_DIR/ca/ca.crt"
    check "MySQL TLS is genuinely enabled"         mysql_tls_actually_on

    check "Postgres without TLS"                   pg_probe sslmode=disable
    check "Postgres with verify-full"              pg_probe sslmode=verify-full "sslrootcert=$CERT_DIR/ca/ca.crt" "host=localhost"
    check "Postgres TLS is genuinely enabled"      pg_tls_actually_on

    check "$FIXTURE_UNIT is active"                unit_active "$FIXTURE_UNIT"
    check "$TCP_UNIT is active"                    unit_active "$TCP_UNIT"
    check "$DNS_UNIT is active"                    unit_active "$DNS_UNIT"
    check "$MYSQL_UNIT is active"                  unit_active "$MYSQL_UNIT"
    [[ -n "$POSTGRES_UNIT" ]] && check "$POSTGRES_UNIT is active" unit_active "$POSTGRES_UNIT"
    check "$BLACKHOLE_UNIT is active"              unit_active "$BLACKHOLE_UNIT"
    check "$FIXTURE_UNIT is enabled"               unit_enabled "$FIXTURE_UNIT"

    check "the manifest parses and is complete"    manifest_parses
    warn  "the clock is NTP-synchronised"          clock_synchronised

    echo
    local row status name detail
    for row in "${CHECK_ROWS[@]}"; do
        status="${row%%|*}"; name="${row#*|}"; detail="${name#*|}"; name="${name%%|*}"
        printf '  %-4s  %-46s %s\n' "$status" "$name" "$detail"
    done
    echo
    echo "  $CHECK_PASS passed, $CHECK_FAIL failed, $CHECK_WARN warned"

    if [[ $CHECK_FAIL -gt 0 ]]; then
        echo
        echo "FAILED: the box is not ready. Fix the rows above and re-run; every step converges, so" >&2
        echo "re-running is always safe." >&2
        return 1
    fi
}

# ==================================================================================================
#  MAIN
# ==================================================================================================

if [[ -n "$ONLY" ]]; then
    echo "    --only $ONLY: skipping the package install"
else
    step_packages
fi

run_step certs     && step_certs
run_step fixture   && step_fixture
run_step nginx     && step_nginx
run_step tcp       && step_tcp
run_step blackhole && step_blackhole
run_step dns       && step_dns
run_step mysql     && step_mysql
run_step postgres  && step_postgres
run_step helper    && step_helper

if [[ $WITH_UI -eq 1 ]] && run_step ui; then
    step_ui
fi

# The manifest is written whenever a full run happens, and on `--only manifest`. A partial run
# deliberately leaves it alone: rewriting it after `--only nginx` would blank POSTGRES_VERSION, which
# only step_postgres discovers.
if [[ -z "$ONLY" || "$ONLY" == "manifest" ]]; then
    write_manifest
fi

restart_app_if_present

if [[ $SELFCHECK -eq 1 ]]; then
    self_check
else
    echo
    echo "Self-check skipped (--no-selfcheck). The box is NOT known to be ready."
fi

cat <<EOF

Targets installed. Next:

  1. If MT-Uptime is not deployed yet, follow deploy/README-deploy.md's "short version" by hand —
     doing it by hand is part of the test. Skip certbot, and leave App__PublicBaseUrl unset.

  2. Complete first-run setup and smoke the install:
       ./e2e/smoke.sh

  3. Run the battery:
       ./e2e/run-tests.sh --tier checker
       ./e2e/run-tests.sh --tier pipeline
       ./e2e/run-tests.sh --tier ui

The manifest every tier reads is $MANIFEST.
Break and restore a target by hand with:
  sudo mt-uptime-e2e-target status
  sudo mt-uptime-e2e-target break http
  sudo mt-uptime-e2e-target restore http
EOF
