#!/usr/bin/env bash
#
# smoke.sh — Tier 0. Prove the installed instance actually works, and complete first-run setup.
#
#   ./smoke.sh [--manifest <path>] [--port <n>] [--no-ratelimit]
#
# Runs against MT-Uptime as deploy/README-deploy.md installs it: the service on 127.0.0.1:5081 and
# nginx in front of it on :80. Nothing here uses the Blazor circuit — every page it touches is static
# SSR and every form is a plain POST — so this tier needs no browser and no .NET.
#
# It does two jobs, and the first one is why it must run before any other tier:
#
#   1. It completes the first-run wizard, which is a ONE-SHOT. The setup token is destroyed the
#      moment an administrator exists, so this is the only chance to capture credentials. They are
#      written to the target manifest as MTU_ADMIN_USER/MTU_ADMIN_PASSWORD, which is what unblocks
#      the Playwright tier — [UIFact] skips itself until those keys exist.
#
#   2. It smoke-tests the install: health on both origins, the first-run guard, the anonymous
#      boundaries, the ping endpoint, the admin exports, the state directory's permissions, and the
#      journal.
#
# ─────────────────────────────────────────────────────────────────────────────────────────────────
#  RE-RUNNABLE, BUT NOT IDEMPOTENT IN THE WAY install-targets.sh IS
#
#  Setup can only happen once per database. On a second run the token file is gone and an
#  administrator exists, so the seven first-run checks are replaced by a single one — that the wizard
#  is genuinely closed — and the script signs in with the credentials it stored last time. That is
#  the honest behaviour: re-running must not report PASS for a sequence it did not perform.
#
#  To get the full first-run sequence back, throw the database away and restart the service:
#      sudo systemctl stop mt-uptime
#      sudo sh -c 'rm -f /var/lib/mt-uptime/mt-uptime.db*'
#      sudo systemctl start mt-uptime
#  The key ring in /var/lib/mt-uptime/keys must survive that, or every stored secret in a restored
#  database becomes undecryptable — which is the failure mode the restore rehearsal exists to catch.
#
#  THE `sh -c` IS LOAD-BEARING, and this advice used to be wrong without it.
#
#  /var/lib/mt-uptime is mode 0700 root-owned — the check table below asserts exactly that. An
#  ordinary user's shell therefore cannot READ the directory, so `mt-uptime.db*` matches nothing and
#  bash passes the pattern through literally. `sudo` then applies to `rm`, not to the glob that was
#  expanded before sudo ever ran, and `rm -f` is handed a file named `mt-uptime.db*`, does not find
#  it, and suppresses the error. Exit 0, nothing deleted, no output.
#
#  The result is worse than a plain failure: the database survives, the administrator survives, this
#  script correctly reports that setup is already complete, and its refusal recommends the same
#  no-op again. Quoting the glob so it expands in the root shell is the whole fix.
# ─────────────────────────────────────────────────────────────────────────────────────────────────
#
#  PRIVILEGE
#
#  Runs as an ordinary user. Three things need root — the setup token, the state directory's
#  contents, and the journal — and all three are read ONCE, up front, before the check table starts.
#  That ordering is deliberate: `check` captures its command's output, so a sudo password prompt
#  fired from inside the table would block on a prompt nobody can see.
#
#  THE RATE-LIMIT CHECKS LEAVE A COOLDOWN
#
#  They run last because they deliberately exhaust two per-IP budgets: 20 sign-ins per 5 minutes and
#  120 pings per minute. Everything on this box shares one partition — the limiter keys on the
#  connection address, and behind nginx that is still 127.0.0.1 — so the UI tier cannot sign in for
#  up to five minutes afterwards. The closing notice says when it is clear; `--no-ratelimit` skips
#  them entirely when you are about to run the UI tier and do not want to wait.
# ─────────────────────────────────────────────────────────────────────────────────────────────────

set -euo pipefail


MANIFEST="${MTU_E2E_MANIFEST:-/etc/mt-uptime-e2e/targets.env}"
APP_PORT=5081
STATE_DIR=/var/lib/mt-uptime
SERVICE=mt-uptime
DO_RATELIMIT=1

# Fixed rather than random, so the account is recognisable in `Users` and in a screenshot.
ADMIN_USER=e2e-admin
ADMIN_EMAIL=e2e-admin@example.test

# Login is capped at 20 per 5 minutes and ping at 120 per minute; the loops below need a ceiling
# above each so a limiter that never engages is reported as a failure rather than looping forever.
LOGIN_ATTEMPT_CEILING=25
PING_ATTEMPT_CEILING=130

while [[ $# -gt 0 ]]; do
    case "$1" in
        --manifest)     MANIFEST="${2:?--manifest needs a path}"; shift 2 ;;
        --manifest=*)   MANIFEST="${1#--manifest=}"; shift ;;
        --port)         APP_PORT="${2:?--port needs a number}"; shift 2 ;;
        --port=*)       APP_PORT="${1#--port=}"; shift ;;
        --no-ratelimit) DO_RATELIMIT=0; shift ;;
        -h|--help)      sed -n '2,64p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; echo "try: $0 --help" >&2; exit 1 ;;
    esac
done

[[ $EUID -eq 0 ]] && echo "note: running as root. That works, but the point of this tier is that an" \
                          "ordinary operator can run it — sudo is used only where it is needed."

ORIGIN="http://127.0.0.1:$APP_PORT"
NGINX="http://127.0.0.1"

# Everything transient lives here: cookie jars, captured privileged reads, response bodies. One
# directory means one trap, and the jars hold a live session cookie so they must not outlive the run.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
chmod 0700 "$WORK"

ANON_JAR="$WORK/anon.jar"
ADMIN_JAR="$WORK/admin.jar"

# ==================================================================================================
#  PREFLIGHT — everything that needs root, and everything that decides which mode we are in
# ==================================================================================================

echo "==> preflight"

command -v curl >/dev/null 2>&1 || { echo "curl is not installed" >&2; exit 1; }

if ! systemctl list-unit-files "$SERVICE.service" --no-legend 2>/dev/null | grep . >/dev/null; then
    {
        echo "REFUSING: there is no $SERVICE.service on this box, so there is nothing to smoke-test."
        echo
        echo "Install MT-Uptime first, by hand, following deploy/README-deploy.md's \"short version\"."
        echo "Doing it by hand is part of the test: every README command that misbehaves is a finding."
    } >&2
    exit 1
fi

# ──────────────────────────────────────────────────────────────────────────────────────────────────
#  WAIT FOR THE APPLICATION BEFORE READING ANYTHING IT WRITES AT STARTUP
#
#  `systemctl start mt-uptime` returns as soon as the process launches. The application then applies
#  migrations — on an empty database that is every table from scratch — and only AFTERWARDS does
#  SetupToken.EnsureAsync write /var/lib/mt-uptime/setup-token.
#
#  Without this wait, a smoke run started immediately after a restart could read that file
#  microseconds before it exists, conclude first-run setup was already complete, and refuse — with
#  advice to delete a database that was already empty.
#
#  /healthz is not merely a convenient gate, it is a SUFFICIENT one: UseMtUptimeAsync applies the
#  migrations and settles the setup token before the first request is served, so an instance that
#  answers has already finished both.
#
#  Added while chasing a "no setup token" that turned out to be the unexpanded-glob problem in the
#  header comment above, not a race at all. It is kept because the race is real regardless — the
#  window is small and the whole point of this script is to run right after a restart — and because
#  it converts "mysteriously refuses" into "waits, then works".
# ──────────────────────────────────────────────────────────────────────────────────────────────────
printf '    waiting for %s to answer /healthz' "$SERVICE"
app_ready=0
for ((attempt = 0; attempt < 60; attempt++)); do
    if curl -s -o /dev/null -m 2 "$ORIGIN/healthz"; then app_ready=1; break; fi
    printf '.'
    sleep 1
done
echo

if [[ $app_ready -eq 0 ]]; then
    {
        echo "REFUSING: $SERVICE did not answer $ORIGIN/healthz within 60s."
        echo
        echo "The service unit exists, so this is not a missing install. Check:"
        echo "    systemctl status $SERVICE"
        echo "    journalctl -u $SERVICE -n 50 --no-pager"
    } >&2
    exit 1
fi

# Read the manifest. It is 0640 root:<test user>, so a user outside that group needs sudo — which is
# fine, but it must be reported clearly rather than surfacing later as a missing variable.
if [[ ! -e "$MANIFEST" ]]; then
    {
        echo "REFUSING: no target manifest at $MANIFEST."
        echo
        echo "Run 'sudo ./e2e/install-targets.sh' first. This tier does not probe the target services,"
        echo "but it writes the admin credentials into that file, and the file has to exist to be"
        echo "written to. Point elsewhere with --manifest or \$MTU_E2E_MANIFEST."
    } >&2
    exit 1
fi

# SC2024 (sudo does not affect redirects) is correct in general and harmless in every case below:
# the redirect is performed by this shell into $WORK, which this shell owns. It is the *reading* that
# needs privilege, and that is what sudo is doing. Piping through `sudo tee` would put root in charge
# of the wrong end.
# shellcheck disable=SC2024
if [[ -r "$MANIFEST" ]]; then
    cp "$MANIFEST" "$WORK/manifest.env"
else
    echo "    manifest is not readable directly; reading it with sudo"
    sudo cat "$MANIFEST" > "$WORK/manifest.env"
fi
# shellcheck source=/dev/null
. "$WORK/manifest.env"

# The setup token, the state directory listing and the journal — captured once, with a visible sudo
# prompt if one is needed, and never touched again from inside a `check`.
echo "    reading the setup token, the state directory and the journal (sudo)"

SETUP_TOKEN=""
if sudo test -f "$STATE_DIR/setup-token"; then
    SETUP_TOKEN="$(sudo cat "$STATE_DIR/setup-token" | tr -d '[:space:]')"
fi

# shellcheck disable=SC2024   # as above: the redirect targets $WORK, which this user owns
sudo ls -la "$STATE_DIR" > "$WORK/state-dir.txt" 2>&1 || true
# shellcheck disable=SC2024
sudo ls -la "$STATE_DIR/keys" > "$WORK/keys-dir.txt" 2>&1 || true
STATE_MODE="$(stat -c %a "$STATE_DIR" 2>/dev/null || echo "?")"

# -p warning is warning-and-above (priorities 0-4). Captured before the checks run so the table is
# reading a fixed snapshot rather than a journal this script is itself still writing to.
# shellcheck disable=SC2024
sudo journalctl -u "$SERVICE" --no-pager -p warning > "$WORK/journal-warn.txt" 2>&1 || true
# shellcheck disable=SC2024
sudo journalctl -u "$SERVICE" --no-pager -p err     > "$WORK/journal-err.txt"   2>&1 || true

# Which mode: a token on disk means the wizard is open and this is a genuine first run.
FIRST_RUN=0
[[ -n "$SETUP_TOKEN" ]] && FIRST_RUN=1

if [[ $FIRST_RUN -eq 1 ]]; then
    # 32 hex characters, from openssl rather than a pipeline. Alphanumeric is not laziness: this
    # password is written into a manifest that other scripts read with `source`, so a '$', a backtick
    # or a space in it would be executed by the shell rather than read.
    #
    # This was `tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32`, which is the idiom everyone reaches
    # for and which CANNOT work under `set -o pipefail`: head closes the pipe after 32 bytes, tr is
    # still writing from an infinite source, tr dies of SIGPIPE with status 141, pipefail promotes
    # that to the pipeline's status, and `set -e` exits the script — silently, mid-preflight, with no
    # message at all. It fired only on the first-run branch, so it was invisible until a box reached
    # exactly this state.
    #
    # openssl rand has no pipeline to break, and it is what install-targets.sh's new_secret() already
    # uses for the database passwords. 128 bits.
    ADMIN_PASSWORD="$(openssl rand -hex 16)"
    echo "    first-run setup is OPEN — the wizard will be completed as '$ADMIN_USER'"
else
    ADMIN_PASSWORD="${MTU_ADMIN_PASSWORD:-}"
    ADMIN_USER="${MTU_ADMIN_USER:-$ADMIN_USER}"
    if [[ -z "$ADMIN_PASSWORD" ]]; then
        {
            echo "REFUSING: first-run setup is already complete, and the manifest has no"
            echo "MTU_ADMIN_PASSWORD from a previous run — so there is no way to sign in."
            echo
            echo "The setup token is destroyed once an administrator exists and cannot be reissued."
            echo "Either supply the credentials by hand:"
            echo "    sudo tee -a $MANIFEST <<< 'MTU_ADMIN_USER=<name>'"
            echo "    sudo tee -a $MANIFEST <<< 'MTU_ADMIN_PASSWORD=<password>'"
            echo "or start from an empty database, which reopens the wizard:"
            echo "    sudo systemctl stop $SERVICE"
            echo "    sudo sh -c 'rm -f $STATE_DIR/mt-uptime.db*'   # keep $STATE_DIR/keys"
            echo "    sudo systemctl start $SERVICE"
        } >&2
        exit 1
    fi
    echo "    first-run setup is already complete — signing in as '$ADMIN_USER' instead"
fi

# ==================================================================================================
#  THE CHECK HARNESS
#
#  Lifted from install-targets.sh, deliberately unchanged. The status is captured through `if`,
#  never as `out=$(...)` followed by `$?`: under `set -euo pipefail` a non-zero exit inside a command
#  substitution propagates out and kills the script, and a smoke test that dies on its first failing
#  check instead of reporting it looks like a crash rather than a result.
# ==================================================================================================

CHECK_PASS=0
CHECK_FAIL=0
CHECK_WARN=0
CHECK_ROWS=()

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

# --- HTTP predicates ------------------------------------------------------------------------------
#
# All of them use --max-time. A hung request in a smoke test is indistinguishable from a hung script.

http_status_is() {  # <expected> <url> [curl args...]
    local want="$1" url="$2"; shift 2
    local got; got="$(curl -s -o /dev/null -m 15 -w '%{http_code}' "$@" "$url")"
    [[ "$got" == "$want" ]] || { echo "expected $want, got $got, for $url"; return 1; }
}

body_contains() {  # <needle> <url> [curl args...]
    local needle="$1" url="$2"; shift 2
    local body; body="$(curl -s -m 15 "$@" "$url")"
    [[ "$body" == *"$needle"* ]] || { echo "'$needle' not in the body of $url"; return 1; }
}

# redirects_to <expected-location-prefix> <url> [curl args...]
#
# Prefix rather than equality because several of these carry a query string the caller does not
# control — /login?error=1&returnUrl=%2Fmonitors%2Fnew, for instance.
#
# "/" is the exception, and it has to be, or the assertion is vacuous: every location starts with a
# slash, so a prefix test for "/" would pass on the /login?error=1 that a *failed* sign-in produces.
# That is the one place where this predicate is asked to tell success from failure, so there it
# compares exactly.
redirects_to() {
    local want="$1" url="$2"; shift 2
    local out code location
    out="$(curl -s -o /dev/null -m 15 -w '%{http_code} %{redirect_url}' "$@" "$url")"
    code="${out%% *}"; location="${out#* }"
    [[ "$code" == 30[1237] ]] || { echo "expected a redirect, got $code for $url"; return 1; }
    # curl reports redirect_url absolute; compare on the path onwards.
    # $APP_PORT quoted inside the expansion: it is a prefix PATTERN, not a string, so an unquoted
    # value carrying a glob character would strip something else entirely.
    location="${location#http://127.0.0.1:"$APP_PORT"}"
    location="${location#http://127.0.0.1}"
    [[ -n "$location" ]] || location="/"

    if [[ "$want" == "/" ]]; then
        [[ "$location" == "/" ]] \
            || { echo "redirected to '$location', expected exactly '/'"; return 1; }
    else
        [[ "$location" == "$want"* ]] \
            || { echo "redirected to '$location', expected something starting '$want'"; return 1; }
    fi
}

# not_status <unwanted> <url> [curl args...] — for a route whose success code is not worth pinning.
not_status() {
    local unwanted="$1" url="$2"; shift 2
    local got; got="$(curl -s -o /dev/null -m 15 -w '%{http_code}' "$@" "$url")"
    [[ "$got" != "$unwanted" ]] || { echo "got $unwanted for $url, which it must not"; return 1; }
}

header_present() {  # <header> <url> [curl args...]
    local name="$1" url="$2"; shift 2
    # `grep -i`, deliberately not `grep -qi`. Under `set -o pipefail`, -q exits on the first match and
    # closes the pipe, curl and tr die of SIGPIPE, and the pipeline reports 141 — so a header that IS
    # present can be reported absent, intermittently, depending on who wins the race. Reading to EOF
    # costs nothing on a response header block.
    curl -sI -m 15 "$@" "$url" | tr -d '\r' | grep -i "^$name:" >/dev/null \
        || { echo "no $name header on $url"; return 1; }
}

# --- antiforgery ----------------------------------------------------------------------------------
#
# Every form in this application posts through UseAntiforgery, so a POST needs a request token from
# the page AND the paired cookie. The cookie jar carries the second half; this pulls the first.
#
# The regex matches the field <AntiforgeryToken/> renders. If it ever stops matching, every POST
# below fails with a redirect to ?error=1 — which is why the harvest itself is a check.
harvest_token() {  # <jar> <url>
    local jar="$1" url="$2" html token
    html="$(curl -s -m 15 -c "$jar" -b "$jar" "$url")"
    token="$(printf '%s' "$html" \
        | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' \
        | head -1 | sed 's/.*value="//; s/"$//')" || true
    [[ -n "$token" ]] || { echo "no __RequestVerificationToken on $url" >&2; return 1; }
    printf '%s' "$token"
}

jar_has_auth_cookie() {  # <jar>
    grep -q 'MT-Uptime\.Auth' "$1" \
        || { echo "no MT-Uptime.Auth cookie in the jar — the sign-in did not take"; return 1; }
}

# ==================================================================================================
#  CHECKS — service, health, and the shape of the deployment
# ==================================================================================================

echo "==> checks"

service_active() {
    systemctl is-active --quiet "$SERVICE" || { echo "$SERVICE is not active"; return 1; }
}

# The exact hostname nginx was provisioned with. provision.sh writes server_name from its argument,
# and the box's own hostname is what that argument almost always was.
BOX_HOST="$(hostname -f 2>/dev/null || hostname)"

check "$SERVICE is active"                        service_active
check "/healthz on the app port"                  http_status_is 200 "$ORIGIN/healthz"
check "/healthz through nginx on :80"             http_status_is 200 "$NGINX/healthz"
check "/healthz with a plain-IP Host header"      http_status_is 200 "$ORIGIN/healthz" -H "Host: 127.0.0.1"
# The one that catches App__PublicBaseUrl having been set: AllowedHosts narrows to the declared name
# plus loopback, and a request carrying the box's own hostname is then answered with 400.
check "/healthz with the box's own Host header"   http_status_is 200 "$ORIGIN/healthz" -H "Host: $BOX_HOST"
# The `--no-restore` failure shape: the app starts, serves HTML, and has no Blazor bundle, so every
# interactive page renders once and is then dead. A 200 here is what says the publish was complete.
#
# Verified against a published Release build, and it has to be: a Debug build started with
# `dotnet run` answers this 500, because MapStaticAssets attaches the framework's development runtime
# handler, which looks for the file under wwwroot/_framework on disk where it has never been written.
# That is a property of running from source, not of the product — the installed instance is always a
# publish — but it will waste an hour for anyone who tries to reproduce this check locally.
check "/_framework/blazor.web.js is served"       http_status_is 200 "$ORIGIN/_framework/blazor.web.js"

# ==================================================================================================
#  CHECKS — first run
# ==================================================================================================

setup_wizard_is_closed() {
    # After an administrator exists, /auth/setup redirects to /login BEFORE it ever looks at the
    # token — AnyUserExistsAsync is checked first. So this proves closure without needing a token,
    # which is the only way to prove it: the token has been destroyed.
    local jar="$WORK/closed.jar" token
    token="$(harvest_token "$jar" "$ORIGIN/setup")" || return 1
    redirects_to "/login" "$ORIGIN/auth/setup" -c "$jar" -b "$jar" \
        --data-urlencode "__RequestVerificationToken=$token" \
        --data-urlencode "setupToken=irrelevant" \
        --data-urlencode "username=should-not-exist" \
        --data-urlencode "email=nope@example.test" \
        --data-urlencode "password=irrelevant-but-long" \
        --data-urlencode "confirm=irrelevant-but-long"
}

if [[ $FIRST_RUN -eq 1 ]]; then

    anon_root_goes_to_setup() { redirects_to "/setup" "$ORIGIN/" -c "$ANON_JAR" -b "$ANON_JAR"; }

    wrong_token_is_refused() {
        local jar="$WORK/badsetup.jar" token
        token="$(harvest_token "$jar" "$ORIGIN/setup")" || return 1
        redirects_to "/setup?error=token" "$ORIGIN/auth/setup" -c "$jar" -b "$jar" \
            --data-urlencode "__RequestVerificationToken=$token" \
            --data-urlencode "setupToken=0000000000000000000000000000000000000000000000000000000000000000" \
            --data-urlencode "username=$ADMIN_USER" \
            --data-urlencode "email=$ADMIN_EMAIL" \
            --data-urlencode "password=$ADMIN_PASSWORD" \
            --data-urlencode "confirm=$ADMIN_PASSWORD"
    }

    complete_setup() {
        local token
        token="$(harvest_token "$ADMIN_JAR" "$ORIGIN/setup")" || return 1
        redirects_to "/" "$ORIGIN/auth/setup" -c "$ADMIN_JAR" -b "$ADMIN_JAR" \
            --data-urlencode "__RequestVerificationToken=$token" \
            --data-urlencode "setupToken=$SETUP_TOKEN" \
            --data-urlencode "username=$ADMIN_USER" \
            --data-urlencode "email=$ADMIN_EMAIL" \
            --data-urlencode "password=$ADMIN_PASSWORD" \
            --data-urlencode "confirm=$ADMIN_PASSWORD" || return 1
        jar_has_auth_cookie "$ADMIN_JAR"
    }

    token_file_is_destroyed() {
        sudo test -f "$STATE_DIR/setup-token" \
            && { echo "$STATE_DIR/setup-token still exists after setup completed"; return 1; }
        return 0
    }

    check "anonymous / redirects to /setup"       anon_root_goes_to_setup
    check "a wrong setup token is refused"        wrong_token_is_refused
    check "/ still redirects after a bad token"   anon_root_goes_to_setup
    check "the correct token completes setup"     complete_setup
    check "the setup token file is destroyed"     token_file_is_destroyed
    check "the setup wizard is now closed"        setup_wizard_is_closed

else
    check "the setup wizard is closed"            setup_wizard_is_closed
fi

# ==================================================================================================
#  Record the credentials NOW
#
#  Before any further check can fail. On a first run these exist in one place — this process's
#  memory — and the wizard that produced them cannot be reopened, so losing them costs a database.
# ==================================================================================================

if [[ $FIRST_RUN -eq 1 ]]; then
    echo "==> recording the administrator in $MANIFEST"

    # Filtered and rewritten rather than appended: a re-run that appended would leave two
    # MTU_ADMIN_PASSWORD lines, and while both `source` and Support/Targets.cs happen to take the
    # last one, relying on that is how two readers drift apart.
    {
        grep -v '^MTU_' "$WORK/manifest.env" || true
        echo "MTU_BASE_URL=$NGINX"
        echo "MTU_APP_URL=$ORIGIN"
        echo "MTU_ADMIN_USER=$ADMIN_USER"
        echo "MTU_ADMIN_EMAIL=$ADMIN_EMAIL"
        echo "MTU_ADMIN_PASSWORD=$ADMIN_PASSWORD"
        echo "MTU_SMOKED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    } > "$WORK/manifest.new"

    sudo install -m 0640 "$WORK/manifest.new" "$MANIFEST"
    # Restore the group the installer set, so the test user can still read it.
    if [[ -n "${E2E_TEST_USER:-}" ]] && id -u "$E2E_TEST_USER" >/dev/null 2>&1; then
        sudo chown "root:$(id -gn "$E2E_TEST_USER")" "$MANIFEST"
    fi
    echo "    MTU_BASE_URL, MTU_ADMIN_USER and MTU_ADMIN_PASSWORD written"
fi

# ==================================================================================================
#  CHECKS — sign-in and the authenticated surface
# ==================================================================================================

sign_in() {  # <jar> <username> <password> -> follows the redirect contract
    local jar="$1" user="$2" pass="$3" token
    rm -f "$jar"
    token="$(harvest_token "$jar" "$ORIGIN/login")" || return 1
    redirects_to "/" "$ORIGIN/auth/login" -c "$jar" -b "$jar" \
        --data-urlencode "__RequestVerificationToken=$token" \
        --data-urlencode "username=$user" \
        --data-urlencode "password=$pass" || return 1
    jar_has_auth_cookie "$jar"
}

sign_in_with_a_wrong_password() {
    local jar="$WORK/badlogin.jar" token
    token="$(harvest_token "$jar" "$ORIGIN/login")" || return 1
    redirects_to "/login?error=1" "$ORIGIN/auth/login" -c "$jar" -b "$jar" \
        --data-urlencode "__RequestVerificationToken=$token" \
        --data-urlencode "username=$ADMIN_USER" \
        --data-urlencode "password=definitely-not-the-password" || return 1
    grep -q 'MT-Uptime\.Auth' "$jar" \
        && { echo "a wrong password still produced an auth cookie"; return 1; }
    return 0
}

# On a re-run there is no session yet, because setup did not run. Sign in before the authenticated
# checks; on a first run the jar already holds the cookie setup issued, and signing in again would
# spend rate-limit budget for nothing.
if [[ $FIRST_RUN -eq 0 ]]; then
    check "signing in with the stored password"   sign_in "$ADMIN_JAR" "$ADMIN_USER" "$ADMIN_PASSWORD"
fi

check "a wrong password is refused"               sign_in_with_a_wrong_password
check "the dashboard renders when signed in"      http_status_is 200 "$ORIGIN/" -b "$ADMIN_JAR"
check "/monitors/new renders when signed in"      http_status_is 200 "$ORIGIN/monitors/new" -b "$ADMIN_JAR"
check "the dashboard renders through nginx"       http_status_is 200 "$NGINX/" -b "$ADMIN_JAR"

# --- anonymous boundaries -------------------------------------------------------------------------
#
# The fallback authorization policy is authenticated-by-default; each of these is a route that must
# NOT have been given an .AllowAnonymous() exemption.

check "anonymous /monitors/new goes to /login"    redirects_to "/login" "$ORIGIN/monitors/new"
check "anonymous /users goes to /login"           redirects_to "/login" "$ORIGIN/users"
check "anonymous /settings goes to /login"        redirects_to "/login" "$ORIGIN/settings"
check "anonymous /admin/backup goes to /login"    redirects_to "/login" "$ORIGIN/admin/backup"
# The circuit gate. MapRazorComponents(...).AllowAnonymous() has to reach the SignalR hub underneath
# it, so this 401 comes from middleware rather than from authorization, and it is the only thing
# stopping an anonymous caller holding a WebSocket open.
check "anonymous /_blazor/negotiate is 401"       http_status_is 401 "$ORIGIN/_blazor/negotiate" -X POST
# The paired exemption, asserted as "not 401" rather than "200": what matters is that the middleware
# lets it through, and blazor.web.js fetches it on every page load including the anonymous ones. Its
# success code depends on whether any JS initializers are registered, which is not this tier's
# business to pin down.
check "/_blazor/initializers stays anonymous"     not_status 401 "$ORIGIN/_blazor/initializers"

# --- the push endpoint ----------------------------------------------------------------------------
#
# Tokens are 128 bits of lower-hex, so 32 zeros is well-formed and certainly unknown. The body
# matters as much as the code: Results.NotFound(string) writes one, and only a bodiless response
# would be re-executed by UseStatusCodePagesWithReExecute into the HTML not-found page.

PING_UNKNOWN=00000000000000000000000000000000
check "an unknown ping token is 404"              http_status_is 404 "$ORIGIN/ping/$PING_UNKNOWN"
check "the 404 says which token is unknown"       body_contains "Unknown ping token" "$ORIGIN/ping/$PING_UNKNOWN"
check "the ping route accepts POST"               http_status_is 404 "$ORIGIN/ping/$PING_UNKNOWN" -X POST
check "the ping route accepts HEAD"               http_status_is 404 "$ORIGIN/ping/$PING_UNKNOWN" -I

# --- admin exports --------------------------------------------------------------------------------

backup_is_a_sqlite_file() {
    # Downloaded to a file first, then read. `curl … | head -c 15` would close the pipe fifteen bytes
    # into a whole SQLite database, curl would die of SIGPIPE, and pipefail would fail this check on a
    # backup that was perfectly good. Reading `head -c` from a FILE opens no pipe at all.
    local out="$WORK/backup.db" magic
    curl -s -m 60 -b "$ADMIN_JAR" -o "$out" "$ORIGIN/admin/backup" \
        || { echo "the backup download failed"; return 1; }
    magic="$(head -c 15 "$out")"
    [[ "$magic" == "SQLite format 3" ]] \
        || { echo "the backup does not begin 'SQLite format 3' (got '$magic', $(wc -c < "$out") bytes)"; return 1; }
}

export_is_json() {
    local body
    body="$(curl -s -m 60 -b "$ADMIN_JAR" "$ORIGIN/admin/export/monitors")"
    # A fresh instance has no monitors, so the content is `[]` — the assertion is that it parses and
    # is an array, not that it holds anything.
    if command -v jq >/dev/null 2>&1; then
        printf '%s' "$body" | jq -e 'type == "array"' >/dev/null \
            || { echo "the export is not a JSON array: ${body:0:120}"; return 1; }
    else
        [[ "${body:0:1}" == "[" ]] \
            || { echo "the export does not look like a JSON array: ${body:0:120}"; return 1; }
    fi
}

check "admin /admin/backup is a SQLite file"      backup_is_a_sqlite_file
check "admin /admin/export/monitors is JSON"      export_is_json

# --- the public status page -----------------------------------------------------------------------
#
# CORRECTED PREDICTION. The plan expected 404 for an unknown slug. PublicStatus.razor sets no status
# code: it renders "This status page is not available." at 200. Asserted as it behaves, and recorded
# as a finding — a 200 for a page that does not exist is wrong for anything that crawls or monitors
# it, and it is a two-line fix in the component.

unknown_status_page_is_200_and_says_so() {
    http_status_is 200 "$ORIGIN/status/definitely-no-such-slug" || return 1
    body_contains "not available" "$ORIGIN/status/definitely-no-such-slug"
}

check "an unknown status slug renders 'not available'" unknown_status_page_is_200_and_says_so

# --- the state directory --------------------------------------------------------------------------

state_dir_is_private() {
    [[ "$STATE_MODE" == "700" ]] \
        || { echo "$STATE_DIR is mode $STATE_MODE, expected 700 — the key ring is inside it"; return 1; }
}

key_ring_exists() {
    grep -qE 'key-[0-9a-fA-F-]+\.xml' "$WORK/keys-dir.txt" \
        || { echo "no key-*.xml in $STATE_DIR/keys: $(tr '\n' ' ' < "$WORK/keys-dir.txt" | cut -c1-120)"; return 1; }
}

database_exists() {
    grep -q 'mt-uptime\.db' "$WORK/state-dir.txt" \
        || { echo "no mt-uptime.db in $STATE_DIR"; return 1; }
}

check "$STATE_DIR is mode 0700"                   state_dir_is_private
check "the Data Protection key ring exists"       key_ring_exists
check "the database is in the state directory"    database_exists

# --- the journal ------------------------------------------------------------------------------------
#
# Split into two rows rather than the plan's single "no warn/fail lines", because those two
# populations are not comparable.
#
#   Errors are unambiguous and fatal to the row.
#
#   Warnings are not. This deployment persists Data Protection keys to the filesystem with no
#   ProtectKeysWith*, which is correct for a single-box install and which the framework warns about
#   on every key creation — twice, in two different categories. The setup banner is also a warning,
#   deliberately, so that it survives the default log level. A row that failed on those would fail on
#   every healthy install, which is worse than not checking. Unexpected warnings are reported as WARN
#   and printed, so a real one is visible without being fatal.

journal_has_no_errors() {
    local lines
    lines="$(grep -vE '^-- (Logs|No entries|Boot)' "$WORK/journal-err.txt" | grep -vE '^\s*$' || true)"
    # A here-string, not `printf '%s'`: wc counts newlines, and a string with no trailing one is
    # reported as zero lines — a report of "0 error line(s)" beside a FAIL is the sort of detail that
    # makes someone doubt the harness rather than read the error.
    [[ -z "$lines" ]] || { echo "$(wc -l <<< "$lines") error line(s): $(tr '\n' ' ' <<< "$lines" | cut -c1-160)"; return 1; }
}

journal_has_no_unexpected_warnings() {
    local lines
    lines="$(grep -vE '^-- (Logs|No entries|Boot)' "$WORK/journal-warn.txt" \
        | grep -vE '^\s*$' \
        | grep -viE 'First-run setup is open' \
        | grep -viE 'one-time token|It is also readable at|is destroyed once the administrator' \
        | grep -viE 'No XML encryptor configured' \
        | grep -viE 'may not be persisted outside of the container' \
        | grep -viE 'Storing keys in a directory' \
        || true)"
    [[ -z "$lines" ]] || { echo "$(wc -l <<< "$lines") unexpected line(s): $(tr '\n' ' ' <<< "$lines" | cut -c1-160)"; return 1; }
}

check "the journal has no error lines"            journal_has_no_errors
warn  "the journal has no unexpected warnings"    journal_has_no_unexpected_warnings

# ==================================================================================================
#  CHECKS — rate limits. LAST, because they exhaust a shared per-IP budget.
# ==================================================================================================

login_limiter_engages() {
    # Bounded, and deliberately not assuming a starting budget: the checks above have already spent
    # some of the 20, and a fixed-window limiter's window may have rolled part-way through. Count up
    # to the ceiling and require that a 429 appears somewhere in it.
    local jar="$WORK/ratelimit.jar" token code i
    for (( i = 1; i <= LOGIN_ATTEMPT_CEILING; i++ )); do
        rm -f "$jar"
        # A token is only harvestable while the GET still works; once the POST limiter engages the
        # login PAGE is still fine, so this keeps working right through.
        token="$(harvest_token "$jar" "$ORIGIN/login")" || return 1
        code="$(curl -s -o /dev/null -m 15 -w '%{http_code}' -c "$jar" -b "$jar" \
            --data-urlencode "__RequestVerificationToken=$token" \
            --data-urlencode "username=$ADMIN_USER" \
            --data-urlencode "password=wrong-on-purpose-$i" \
            "$ORIGIN/auth/login")"
        [[ "$code" == "429" ]] && { echo "429 after $i attempts"; return 0; }
    done
    echo "no 429 after $LOGIN_ATTEMPT_CEILING sign-in attempts — the login limiter did not engage"
    return 1
}

ping_limiter_engages() {
    local code i
    for (( i = 1; i <= PING_ATTEMPT_CEILING; i++ )); do
        code="$(curl -s -o /dev/null -m 15 -w '%{http_code}' "$ORIGIN/ping/$PING_UNKNOWN")"
        [[ "$code" == "429" ]] && return 0
    done
    echo "no 429 after $PING_ATTEMPT_CEILING pings — the ping limiter did not engage"
    return 1
}

ping_429_carries_retry_after() {
    header_present "Retry-After" "$ORIGIN/ping/$PING_UNKNOWN"
}

if [[ $DO_RATELIMIT -eq 1 ]]; then
    echo "    rate-limit checks (these take a few seconds and leave a cooldown)"
    check "the ping limiter engages"              ping_limiter_engages
    check "the 429 carries Retry-After"           ping_429_carries_retry_after
    check "the sign-in limiter engages"           login_limiter_engages
else
    CHECK_ROWS+=("SKIP|rate limits|--no-ratelimit was given")
fi

# ==================================================================================================
#  REPORT
# ==================================================================================================

echo
for row in "${CHECK_ROWS[@]}"; do
    status="${row%%|*}"; name="${row#*|}"; detail="${name#*|}"; name="${name%%|*}"
    printf '  %-4s  %-46s %s\n' "$status" "$name" "$detail"
done
echo
echo "  $CHECK_PASS passed, $CHECK_FAIL failed, $CHECK_WARN warned"

if [[ $CHECK_FAIL -gt 0 ]]; then
    {
        echo
        echo "FAILED: the install is not proven. Fix the rows above and re-run."
        echo
        echo "Re-running is safe. First-run setup is a one-shot, so it will be skipped and the stored"
        echo "credentials used instead — the row that says so is 'the setup wizard is closed'."
    } >&2
    exit 1
fi

cat <<EOF

Tier 0 passed. The manifest now carries the administrator, so the UI tier will no longer skip:

  MTU_BASE_URL=$NGINX
  MTU_ADMIN_USER=$ADMIN_USER
  MTU_ADMIN_PASSWORD  (in $MANIFEST, mode 0640)

Next:
  ./e2e/run-tests.sh --tier checker
  ./e2e/run-tests.sh --tier pipeline
  ./e2e/run-tests.sh --tier ui
EOF

if [[ $DO_RATELIMIT -eq 1 ]]; then
    cat <<EOF

⚠ Cooldown. The checks above deliberately exhausted two per-IP budgets, and everything on this box
  shares one partition:

    sign-in   20 per 5 minutes   — the UI tier cannot log in until $(date -u -d '+5 minutes' +%H:%M) UTC
    ping     120 per minute      — the Push scenario will 429 until $(date -u -d '+1 minute' +%H:%M) UTC

  Run the checker and pipeline tiers now; they need neither. Use --no-ratelimit on a re-run when you
  are going straight to the UI tier.
EOF
fi
