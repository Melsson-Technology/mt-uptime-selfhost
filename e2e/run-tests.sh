#!/usr/bin/env bash
#
# run-tests.sh — run one tier of the E2E battery.
#
#   ./run-tests.sh [--tier checker|pipeline|ui|all] [--manifest <path>] [--filter <expr>]
#                  [--list] [--no-build] [-- <extra dotnet test args>]
#
# Tests.E2E.MT-Uptime is deliberately NOT in MT-Uptime.Engine.slnx, so `scripts/test.sh` still runs
# exactly the 360 hermetic tests it promises. This script is the only supported way to run the other
# suite, and it exists mostly to do three things nobody should have to remember:
#
#   1. Export MTU_E2E_MANIFEST when the manifest is somewhere other than the default, so
#      Support/Targets.cs finds it.
#   2. Refuse early, and say why, when the box is not ready — an unprepared machine reports every
#      test SKIPPED, which is correct behaviour and a confusing thing to stare at.
#   3. Install the Playwright browsers before the UI tier, from the built output, which is the one
#      step with no obvious command.
#
# ─────────────────────────────────────────────────────────────────────────────────────────────────
#  WHY A TIER IS A NAMESPACE, NOT A TRAIT
#
#  The tiers are Checkers/, Pipeline/ and Ui/, and each maps to a namespace under
#  MT.Uptime.Tests.E2E. Filtering on FullyQualifiedName~ therefore needs no attribute on any test
#  and cannot drift from the directory layout — a test in Pipeline/ is in the pipeline tier because
#  of where its file lives, which is the one thing nobody forgets to do.
# ─────────────────────────────────────────────────────────────────────────────────────────────────

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENGINE="$(cd "$HERE/.." && pwd)"
PROJECT="$ENGINE/Tests.E2E.MT-Uptime"

MANIFEST="${MTU_E2E_MANIFEST:-/etc/mt-uptime-e2e/targets.env}"
TIER=all
FILTER=""
LIST=0
NO_BUILD=0
PASSTHROUGH=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tier)       TIER="${2:?--tier needs a value}"; shift 2 ;;
        --tier=*)     TIER="${1#--tier=}"; shift ;;
        --manifest)   MANIFEST="${2:?--manifest needs a path}"; shift 2 ;;
        --manifest=*) MANIFEST="${1#--manifest=}"; shift ;;
        --filter)     FILTER="${2:?--filter needs an expression}"; shift 2 ;;
        --filter=*)   FILTER="${1#--filter=}"; shift ;;
        --list)       LIST=1; shift ;;
        --no-build)   NO_BUILD=1; shift ;;
        --)           shift; PASSTHROUGH=("$@"); break ;;
        -h|--help)    sed -n '2,28p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; echo "try: $0 --help" >&2; exit 1 ;;
    esac
done

case "$TIER" in
    checker|pipeline|ui|all) ;;
    *) echo "unknown tier '$TIER' — expected checker, pipeline, ui or all" >&2; exit 1 ;;
esac

# ==================================================================================================
#  PREFLIGHT
# ==================================================================================================

[[ -d "$PROJECT" ]] || { echo "no $PROJECT — is this the engine tree?" >&2; exit 1; }

if ! command -v dotnet >/dev/null 2>&1; then
    {
        echo "REFUSING: dotnet is not on PATH."
        echo
        echo "Ubuntu's apt SDK may be on a feature band older than global.json asks for, so install it"
        echo "the way the runbook does:"
        echo "    curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"
        echo "    export PATH=\$HOME/.dotnet:\$PATH"
    } >&2
    exit 1
fi

# The whole battery skips itself without this file, so a missing one is worth refusing over rather
# than reporting as 200 skipped tests — which looks identical to a suite that has not been written.
if [[ ! -e "$MANIFEST" ]]; then
    {
        echo "REFUSING: no target manifest at $MANIFEST."
        echo
        echo "Every test in this project skips itself when the manifest is absent — that is by design,"
        echo "so the suite is harmless on a developer's laptop — but it means running it here would"
        echo "report success without testing anything."
        echo
        echo "    sudo ./e2e/install-targets.sh --with-ui"
        echo
        echo "Point elsewhere with --manifest or \$MTU_E2E_MANIFEST."
    } >&2
    exit 1
fi

if [[ ! -r "$MANIFEST" ]]; then
    {
        echo "REFUSING: $MANIFEST exists but this user cannot read it."
        echo
        echo "It is 0640 root:<test user>. Add yourself to that group, or re-run install-targets.sh"
        echo "with E2E_TEST_USER set to the account you intend to run the tests as. Do NOT run the"
        echo "tests as root: the break/restore helper is reached through sudo on purpose, and running"
        echo "as root would stop proving that the sudoers rule works."
    } >&2
    exit 1
fi

export MTU_E2E_MANIFEST="$MANIFEST"

# shellcheck source=/dev/null
. "$MANIFEST"

if [[ "$TIER" == "ui" || "$TIER" == "all" ]]; then
    if [[ -z "${MTU_ADMIN_PASSWORD:-}" || -z "${MTU_BASE_URL:-}" ]]; then
        if [[ "$TIER" == "ui" ]]; then
            {
                echo "REFUSING: the manifest has no MTU_BASE_URL/MTU_ADMIN_PASSWORD, so every UI test"
                echo "would skip."
                echo
                echo "Those are written by smoke.sh when it completes first-run setup:"
                echo "    ./e2e/smoke.sh"
            } >&2
            exit 1
        fi
        echo "note: no MTU_ADMIN_PASSWORD in the manifest — the UI tier will skip. Run ./e2e/smoke.sh."
    fi
fi

# ==================================================================================================
#  BUILD, AND THE PLAYWRIGHT BROWSERS
# ==================================================================================================

BUILD_ARGS=(-c Release)
# The sandbox this was developed in blocks MSBuild worker-node spawning; -m:1 costs nothing on a real
# box and turns a silent "exited 1 having built nothing while reporting 0 errors" into a normal build.
# Kept because a box that behaves that way is impossible to diagnose from the output alone.
BUILD_ARGS+=(-m:1)

if [[ $NO_BUILD -eq 0 ]]; then
    echo "==> building $(basename "$PROJECT")"
    dotnet build "$PROJECT" "${BUILD_ARGS[@]}" --nologo
fi

OUT="$PROJECT/bin/Release/net10.0"

# ─────────────────────────────────────────────────────────────────────────────────────────────────
#  INSTALLING CHROMIUM, AND WHY IT IS NOT ONE COMMAND
#
#  `dotnet <out>/Microsoft.Playwright.dll install chromium` — the obvious invocation, and the one
#  this plan called for — CANNOT work. Microsoft.Playwright is a class library: it has an entry
#  point in its metadata but ships no runtimeconfig.json, so the host refuses it as "self-contained"
#  and looks for hostpolicy.dll beside it. Verified, not assumed.
#
#  Playwright's own answer is the playwright.ps1 dropped into the build output, which loads the
#  assembly and calls Microsoft.Playwright.Program.Main. That needs pwsh, which stock Ubuntu 24.04
#  does not have. So: use pwsh when it is there, and otherwise do exactly what that script does —
#  run the same entry point under the TEST assembly's runtimeconfig and deps, which is where the
#  framework reference and the driver's assets actually resolve from.
#
#  PLAYWRIGHT_DRIVER_SEARCH_PATH points at the output directory, which is where the .playwright/
#  folder lands. That folder holds only the node binaries for the RIDs Directory.Build.props
#  declares (linux-x64, linux-arm64) — so this step reports "missing required assets" on a Windows
#  dev machine and works on the box, which is the only place the UI tier runs.
# ─────────────────────────────────────────────────────────────────────────────────────────────────

install_chromium() {
    local out="$1"
    export PLAYWRIGHT_DRIVER_SEARCH_PATH="$out"

    if command -v pwsh >/dev/null 2>&1 && [[ -f "$out/playwright.ps1" ]]; then
        pwsh "$out/playwright.ps1" install chromium
        return
    fi

    dotnet exec \
        --runtimeconfig "$out/MT.Uptime.Tests.E2E.runtimeconfig.json" \
        --depsfile "$out/MT.Uptime.Tests.E2E.deps.json" \
        "$out/Microsoft.Playwright.dll" install chromium
}

if [[ "$TIER" == "ui" || "$TIER" == "all" ]] && [[ $LIST -eq 0 ]]; then
    # Chromium only. The tier is one browser by decision; the other two are ~400 MB nobody asked for.
    if [[ -f "$OUT/Microsoft.Playwright.dll" ]]; then
        echo "==> ensuring Chromium is installed for Playwright"
        if ! install_chromium "$OUT"; then
            {
                echo
                echo "Playwright could not install Chromium."
                echo "If that was a shared-library failure, the OS dependencies come from:"
                echo "    sudo ./e2e/install-targets.sh --with-ui --only ui"
            } >&2
            # Fatal only when the UI tier is the point. On --tier all the other two tiers are worth
            # running regardless, and the UI tests will skip themselves rather than fail.
            [[ "$TIER" == "ui" ]] && exit 1
            echo "continuing without a browser — the UI tests will skip" >&2
        fi
    elif [[ "$TIER" == "ui" ]]; then
        echo "REFUSING: no Microsoft.Playwright.dll in $OUT — the UI tier is not built yet." >&2
        exit 1
    else
        echo "note: no Microsoft.Playwright.dll in $OUT — the UI tier is not built yet"
    fi
fi

# ==================================================================================================
#  RUN
# ==================================================================================================

case "$TIER" in
    checker)  TIER_FILTER='FullyQualifiedName~MT.Uptime.Tests.E2E.Checkers' ;;
    pipeline) TIER_FILTER='FullyQualifiedName~MT.Uptime.Tests.E2E.Pipeline' ;;
    ui)       TIER_FILTER='FullyQualifiedName~MT.Uptime.Tests.E2E.Ui' ;;
    all)      TIER_FILTER='' ;;
esac

# --filter narrows within the tier rather than replacing it, so `--tier checker --filter Dns` means
# what it looks like.
if [[ -n "$FILTER" ]]; then
    if [[ -n "$TIER_FILTER" ]]; then
        TIER_FILTER="$TIER_FILTER&$FILTER"
    else
        TIER_FILTER="$FILTER"
    fi
fi

TEST_ARGS=("$PROJECT" -c Release -m:1 --nologo)
[[ $NO_BUILD -eq 1 ]] && TEST_ARGS+=(--no-build)
[[ -n "$TIER_FILTER" ]] && TEST_ARGS+=(--filter "$TIER_FILTER")

if [[ $LIST -eq 1 ]]; then
    echo "==> tests in tier '$TIER'"
    exec dotnet test "${TEST_ARGS[@]}" --list-tests
fi

# ─────────────────────────────────────────────────────────────────────────────────────────────────
#  AN EMPTY TIER MUST NOT LOOK LIKE A PASSING ONE
#
#  `dotnet test --filter <matches nothing>` prints "No test matches the given testcase filter" and
#  exits ZERO. Measured, not assumed. So a renamed namespace, a mistyped --filter, or a tier that
#  simply has not been written yet all report success having executed nothing — which is the exact
#  failure the plan's own acceptance rule is about ("a scenario that cannot fail is deleted").
#
#  A discovery pass costs a couple of seconds and turns that into a refusal that says which case it
#  is. It runs against the already-built output, so it is not a second build.
# ─────────────────────────────────────────────────────────────────────────────────────────────────

DISCOVERY_ARGS=("$PROJECT" -c Release -m:1 --nologo --no-build --list-tests)
[[ -n "$TIER_FILTER" ]] && DISCOVERY_ARGS+=(--filter "$TIER_FILTER")

DISCOVERED="$(dotnet test "${DISCOVERY_ARGS[@]}" 2>/dev/null \
    | grep -cE '^\s+MT\.Uptime\.Tests\.E2E\.' || true)"

if [[ "${DISCOVERED:-0}" -eq 0 ]]; then
    {
        echo "REFUSING: nothing to run — no test matched the tier '$TIER'."
        echo
        if [[ -n "$FILTER" ]]; then
            echo "The filter was: $TIER_FILTER"
            echo "Check --filter '$FILTER' against ./run-tests.sh --tier $TIER --list."
        else
            echo "Tier '$TIER' maps to the namespace MT.Uptime.Tests.E2E.$(
                case "$TIER" in checker) echo Checkers ;; pipeline) echo Pipeline ;; ui) echo Ui ;; *) echo '<any>' ;; esac)"
            echo "and no test is in it. Either that tier has not been written yet, or its files moved"
            echo "out of the matching directory."
        fi
        echo
        echo "Refusing rather than reporting a pass: 'dotnet test' exits 0 when its filter matches"
        echo "nothing, so an empty tier is indistinguishable from a green one."
    } >&2
    exit 1
fi

TEST_ARGS+=(--logger "console;verbosity=normal")
[[ ${#PASSTHROUGH[@]} -gt 0 ]] && TEST_ARGS+=("${PASSTHROUGH[@]}")

echo "==> running tier '$TIER'"
echo "    manifest:   $MANIFEST"
echo "    discovered: $DISCOVERED test(s)"
[[ -n "$TIER_FILTER" ]] && echo "    filter:     $TIER_FILTER"
echo

# The targets are shared and singular, so a tier that failed part-way can leave one broken. Restoring
# on the way out costs a second and turns "the next run failed too" into "the next run passed".
restore_targets() {
    local helper="${E2E_HELPER:-/usr/local/bin/mt-uptime-e2e-target}"
    [[ -x "$helper" ]] || return 0
    sudo -n "$helper" restore all >/dev/null 2>&1 || true
}
trap restore_targets EXIT

dotnet test "${TEST_ARGS[@]}"
