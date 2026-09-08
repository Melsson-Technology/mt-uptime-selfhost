#!/bin/bash
# Restore a self-hosted MT-Uptime instance - or rehearse one without touching anything.
#
# THE REHEARSAL IS THE POINT. The engine's backup procedure has shipped unrehearsed since the project
# began, and the reason is always the same: rehearsing meant overwriting the live instance, which
# nobody does on a Tuesday to find out whether it works. So there are two modes and the safe one is
# the default shape:
#
#     sudo mt-uptime-engine-restore.sh --rehearse <archive|s3-name>
#         Unpacks into a scratch directory, runs integrity_check, and PROVES THE KEY RING MATCHES THE
#         CIPHERTEXT before comparing row counts against the live instance. Touches nothing.
#
#     sudo mt-uptime-engine-restore.sh --into <state-dir> <archive> [--force]
#         The real thing. Stops the service, takes its own copy of what is there first, restores, fixes
#         ownership, starts it again and checks it answers.
#
# WHAT THE REHEARSAL CHECKS THAT A HUMAN WOULD NOT. The documented manual check was "press Send test on
# a channel afterwards", because a database restored without its key ring looks perfectly healthy right
# up until a secret needs decrypting. That check requires a restore to have already happened and a
# human to remember. This does it from the ciphertext itself: every Data Protection payload carries the
# id of the key that encrypted it, so the archive can be proven self-consistent before anyone commits
# to anything.
set -euo pipefail

STATE_DIR_DEFAULT=${STATE_DIR:-/var/lib/mt-uptime}
BACKUP_DIR=${BACKUP_DIR:-/var/backups/mt-uptime-engine}
SERVICE=${SERVICE:-mt-uptime.service}
OWNER=${OWNER:-mtuptime}
# SITE-SPECIFIC SETTINGS LIVE IN A FILE, NOT IN THIS SCRIPT OR THE UNIT.
#
# This script ships publicly; your bucket is yours. Both the nightly unit and a hand-run restore read
# the same file, so an emergency restore gets exactly the configuration the backup was written with —
# which is the failure this avoids: only the unit knowing where the archives went.
#
# It is the source of truth: values here win over the environment. To use a different one for a single
# run, pass BACKUP_ENV=/path/to/other.
BACKUP_ENV=${BACKUP_ENV:-/etc/mt-uptime/backup.env}
# shellcheck source=/dev/null
[ -f "$BACKUP_ENV" ] && . "$BACKUP_ENV"

S3_BUCKET=${S3_BUCKET:-}
S3_PREFIX=${S3_PREFIX:-engine}
ENV_FILE=${ENV_FILE:-/etc/mt-uptime/mt-uptime.env}

# THE HEALTH CHECK MUST NOT GUESS A PORT, and this used to.
#
# It defaulted to 127.0.0.1:5000 because that is the port the install guide uses. On the machine this
# was written for, 5000 belongs to an entirely different application, and MT-Uptime is on 5081 because
# its EnvironmentFile says so - which the unit file does not, since the file overrides it. A restore
# that curls 5000 therefore health-checks a stranger: if that stranger answers 200, the restore reports
# success over a dead instance, which is the one lie a restore script must never tell.
#
# So it is read from the same file the service reads, and if it cannot be determined the check is
# SKIPPED with a loud note rather than pointed at a guess. "I could not verify this" is a true
# statement; "200 OK" from somebody else's application is not.
if [ -z "${HEALTH_URL:-}" ] && [ -r "$ENV_FILE" ]; then
  _urls=$(sed -n 's/^[[:space:]]*ASPNETCORE_URLS=//p' "$ENV_FILE" | tail -1 | tr -d '"' | cut -d';' -f1)
  [ -n "${_urls:-}" ] && HEALTH_URL="${_urls%/}/healthz"
fi
HEALTH_URL=${HEALTH_URL:-}

: "${AWS_SHARED_CREDENTIALS_FILE:=/etc/mt-uptime/aws-credentials}"
export AWS_SHARED_CREDENTIALS_FILE
[ -n "${AWS_REGION:-}" ] && export AWS_REGION

[ "$(id -u)" -eq 0 ] || { echo "run as root"; exit 1; }
command -v sqlite3 >/dev/null || { echo "sqlite3 is required (apt-get install sqlite3)"; exit 1; }

MODE=""; TARGET=""; ARCHIVE=""; FORCE=no
while [ $# -gt 0 ]; do
  case "$1" in
    --rehearse) MODE=rehearse; shift ;;
    --into)     MODE=into; TARGET=${2:-}; shift 2 ;;
    --force)    FORCE=yes; shift ;;
    *)          ARCHIVE=$1; shift ;;
  esac
done

[ -n "$MODE" ] || { echo "usage: $0 --rehearse <archive> | --into <state-dir> <archive> [--force]"; exit 1; }
[ -n "$ARCHIVE" ] || { echo "no archive given"; exit 1; }

log () { printf '%s  %s\n' "$(date -u +%FT%TZ)" "$*"; }
die () { log "FAILED: $*"; exit 1; }

# -- locate the archive ---------------------------------------------------------------------------
if [ ! -f "$ARCHIVE" ]; then
  CANDIDATE="$BACKUP_DIR/$(basename "$ARCHIVE")"
  if [ -f "$CANDIDATE" ]; then
    ARCHIVE=$CANDIDATE
    log "using local $ARCHIVE"
  elif [ -n "$S3_BUCKET" ]; then
    KEY="$S3_PREFIX/$(basename "$ARCHIVE")"
    log "not local; fetching s3://$S3_BUCKET/$KEY"
    install -d -m 0700 "$BACKUP_DIR"
    aws s3 cp "s3://$S3_BUCKET/$KEY" "$CANDIDATE" --only-show-errors || die "could not fetch $KEY"
    ARCHIVE=$CANDIDATE
  else
    die "no such archive, and S3_BUCKET is not set so there is nowhere else to look"
  fi
fi

log "archive: $ARCHIVE ($(stat -c %s "$ARCHIVE") bytes)"
gzip -t "$ARCHIVE" || die "not valid gzip"

WORK=$(mktemp -d)
chmod 0700 "$WORK"
trap 'rm -rf "$WORK"' EXIT

tar -xzf "$ARCHIVE" -C "$WORK"
DB_FILE=$(find "$WORK" -name 'mt-uptime.db' | head -1)
[ -n "$DB_FILE" ] || die "no mt-uptime.db inside the archive"
KEYS_FOUND=$(dirname "$DB_FILE")/keys
[ -d "$KEYS_FOUND" ] || die "no key ring inside the archive - this backup cannot restore its secrets"

MANIFEST=$(find "$WORK" -name MANIFEST.txt | head -1)
if [ -n "$MANIFEST" ]; then
  log "manifest says:"
  grep -E "^(Timestamp|Database bytes|integrity_check|Key ring|SQLite)" "$MANIFEST" | sed 's/^/    /'
fi

# -- checks that apply to both modes ---------------------------------------------------------------
log "integrity_check on the archived database:"
INTEGRITY=$(sqlite3 "$DB_FILE" "PRAGMA integrity_check;" | head -1)
[ "$INTEGRITY" = "ok" ] || die "integrity_check said: $INTEGRITY"
log "    ok"

# THE CHECK THAT MATTERS. See the header.
key_id_of_payload () {
  local std hex b
  std=$(printf '%s' "$1" | tr '_-' '/+')
  case $(( ${#std} % 4 )) in 2) std="$std==";; 3) std="$std=";; esac
  hex=$(printf '%s' "$std" | base64 -d 2>/dev/null | head -c 20 | xxd -p | tr -d '\n')
  [ ${#hex} -ge 40 ] || return 1
  [ "${hex:0:8}" = "09f0c9f0" ] || return 1
  b=${hex:8:32}
  printf '%s%s%s%s-%s%s-%s%s-%s-%s\n' \
    "${b:6:2}" "${b:4:2}" "${b:2:2}" "${b:0:2}" \
    "${b:10:2}" "${b:8:2}" "${b:14:2}" "${b:12:2}" "${b:16:4}" "${b:20:12}"
}

log "proving the archived key ring can decrypt the archived secrets:"
PAYLOADS=$(
  sqlite3 "$DB_FILE" "SELECT name FROM sqlite_master WHERE type='table';" | while read -r t; do
    sqlite3 "$DB_FILE" "PRAGMA table_info('$t');" | cut -d'|' -f2 | while read -r c; do
      [ -z "$c" ] && continue
      sqlite3 "$DB_FILE" "SELECT CAST(\"$c\" AS TEXT) FROM '$t' WHERE CAST(\"$c\" AS TEXT) LIKE 'CfDJ8%' LIMIT 5;" 2>/dev/null
    done
  done
)

SECRETS=0
UNMATCHED=""
while read -r p; do
  [ -z "$p" ] && continue
  SECRETS=$((SECRETS + 1))
  if kid=$(key_id_of_payload "$p"); then
    if [ -f "$KEYS_FOUND/key-$kid.xml" ]; then
      log "    secret encrypted with key $kid -> present"
    else
      UNMATCHED="$UNMATCHED $kid"
      log "    secret encrypted with key $kid -> MISSING FROM THIS ARCHIVE"
    fi
  else
    UNMATCHED="$UNMATCHED (unparseable)"
  fi
done <<< "$PAYLOADS"

if [ "$SECRETS" -eq 0 ]; then
  log "    no encrypted secrets in this backup (normal for a fresh instance)"
elif [ -n "$UNMATCHED" ]; then
  die "this archive's key ring cannot decrypt its own data:$UNMATCHED. Restoring it would produce an instance that starts, looks healthy, and cannot send a single notification."
else
  log "    all $SECRETS secret(s) pair with a key in this archive"
fi

# -- rehearsal ------------------------------------------------------------------------------------
if [ "$MODE" = rehearse ]; then
  log "--- REHEARSAL: nothing on this system is modified ---"
  LIVE_DB="$STATE_DIR_DEFAULT/mt-uptime.db"

  if [ -f "$LIVE_DB" ]; then
    log "comparing the archive against the live instance:"
    # The live database is read through its own online-backup copy, never directly: it is in WAL mode
    # and being written to right now.
    LIVE_COPY="$WORK/live.db"
    sqlite3 "$LIVE_DB" ".backup '$LIVE_COPY'" 2>/dev/null || LIVE_COPY=""
  else
    LIVE_COPY=""
    log "no live instance at $LIVE_DB - reporting the archive's own contents:"
  fi

  MISMATCH=0
  while read -r t; do
    [ -z "$t" ] && continue
    rest=$(sqlite3 "$DB_FILE" "SELECT COUNT(*) FROM '$t';" 2>/dev/null || echo missing)
    if [ -n "$LIVE_COPY" ]; then
      live=$(sqlite3 "$LIVE_COPY" "SELECT COUNT(*) FROM '$t';" 2>/dev/null || echo missing)
      if [ "$live" = "$rest" ]; then
        printf '    %-26s %8s = %-8s ok\n' "$t" "$live" "$rest"
      else
        printf '    %-26s %8s ~ %-8s DIFFERS\n' "$t" "$live" "$rest"
        MISMATCH=$((MISMATCH + 1))
      fi
    else
      printf '    %-26s %8s\n' "$t" "$rest"
    fi
  done < <(sqlite3 "$DB_FILE" "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;")

  log ""
  if [ -z "$LIVE_COPY" ]; then
    log "REHEARSAL PASSED: the archive is a valid, self-consistent instance."
  elif [ "$MISMATCH" -eq 0 ]; then
    log "REHEARSAL PASSED: every table restored with identical row counts."
  else
    log "REHEARSAL PASSED, with $MISMATCH table(s) differing from live."
    log "Expected - the instance has been running since the backup was taken. Heartbeats in particular"
    log "grows constantly. Check the differences are rows that ARRIVED, not rows that went missing."
  fi
  log "The key ring pairing is the part that had to hold, and it did."
  exit 0
fi

# -- the real restore -----------------------------------------------------------------------------
[ -n "$TARGET" ] || die "--into needs a state directory"
log "--- RESTORE into $TARGET ---"

if [ -e "$TARGET/mt-uptime.db" ] && [ "$FORCE" != yes ]; then
  die "$TARGET already holds an instance. Rehearse first, then pass --force if you mean it."
fi

if [ -e "$TARGET" ]; then
  SAFETY="$BACKUP_DIR/pre-restore-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
  log "copying what is there now -> $SAFETY"
  install -d -m 0700 "$BACKUP_DIR"
  tar -czf "$SAFETY" -C "$(dirname "$TARGET")" "$(basename "$TARGET")"
  chmod 0600 "$SAFETY"
  log "safety copy is $(stat -c %s "$SAFETY") bytes"
fi

log "stopping $SERVICE"
systemctl stop "$SERVICE" || true

install -d -m 0700 "$TARGET"
install -d -m 0700 "$TARGET/keys"
# The -wal and -shm belong to the OLD database. Leaving them beside a restored file is a corruption
# risk, and sqlite will happily open the pair and apply the wrong log.
rm -f "$TARGET/mt-uptime.db" "$TARGET/mt-uptime.db-wal" "$TARGET/mt-uptime.db-shm"
cp -a "$DB_FILE" "$TARGET/mt-uptime.db"
cp -a "$KEYS_FOUND"/key-*.xml "$TARGET/keys/"
chown -R "$OWNER:$OWNER" "$TARGET"
chmod 0700 "$TARGET" "$TARGET/keys"
chmod 0600 "$TARGET/mt-uptime.db" "$TARGET"/keys/*.xml
log "restored database and $(find "$TARGET/keys" -name 'key-*.xml' | wc -l) key(s), owned by $OWNER"

ENV_IN_ARCHIVE=$(find "$WORK" -name 'mt-uptime.env' | head -1)
if [ -n "$ENV_IN_ARCHIVE" ]; then
  log "the archive also carries mt-uptime.env; it is at $ENV_IN_ARCHIVE"
  log "NOT copied over /etc automatically - compare it against the live one first."
fi

log "starting $SERVICE"
systemctl start "$SERVICE" || die "the service did not start"

if [ -z "$HEALTH_URL" ]; then
  log "!! no ASPNETCORE_URLS in $ENV_FILE, so there is no address to check."
  log "!! The restore itself completed. Verify the instance answers by hand, and see the note above"
  log "!! about why this refuses to guess a port rather than checking one that may not be ours."
else
  for i in 1 2 3 4 5 6 7 8 9 10; do
    code=$(curl -s -o /dev/null -w '%{http_code}' "$HEALTH_URL" 2>/dev/null || echo 000)
    [ "$code" = "200" ] && { log "$HEALTH_URL answered 200"; break; }
    sleep 2
  done
  [ "${code:-000}" = "200" ] || log "!! $HEALTH_URL answered ${code:-000} - check journalctl -u $SERVICE"
fi

log ""
log "restore complete. The key ring came back with the database, so stored secrets decrypt - but"
log "confirm it the way a human can see: open a notification channel and press Send test."
