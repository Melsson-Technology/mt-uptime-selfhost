#!/bin/bash
# Back up a self-hosted MT-Uptime instance: the database AND the Data Protection key ring.
#
# THE KEY RING IS THE POINT, AND IT IS THE PART EVERY HAND-ROLLED BACKUP MISSES. The database stores
# secrets — the SendGrid API key, notification channel credentials — encrypted with a key that lives in
# a separate directory. Copy the database alone and the restored instance starts, reports healthy,
# shows every monitor, and then silently cannot decrypt a single secret. There is no error at boot; you
# find out when an alert does not go out.
#
# So this backs up both, in one archive, and then proves they belong together:
#
#   THE PAIRING CHECK. Every ASP.NET Core Data Protection payload begins with a four-byte magic number
#   followed by the 16-byte id of the key that encrypted it. This script reads that id out of the
#   secrets actually stored in the database and asserts a matching key-<id>.xml is in the archive. A
#   backup whose key ring does not match its own ciphertext FAILS HERE, rather than three months later.
#
# No downtime: sqlite3's .backup uses the online backup API, so the copy is consistent against a
# running instance without blocking it. Do not replace it with `cp` — the database is in WAL mode and a
# plain copy of a live one is a coin flip.
set -euo pipefail

STATE_DIR=${STATE_DIR:-/var/lib/mt-uptime}
DB=${DB:-$STATE_DIR/mt-uptime.db}
KEYS_DIR=${KEYS_DIR:-$STATE_DIR/keys}
ENV_FILE=${ENV_FILE:-/etc/mt-uptime/mt-uptime.env}
BACKUP_DIR=${BACKUP_DIR:-/var/backups/mt-uptime-engine}
LOCAL_RETENTION_DAYS=${LOCAL_RETENTION_DAYS:-7}

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

# Off-box storage. Leave S3_BUCKET empty for a local-only backup — the script will say loudly that
# nothing left the machine, because a backup on the same disk as the thing it protects is not one.
S3_BUCKET=${S3_BUCKET:-}
S3_PREFIX=${S3_PREFIX:-engine}

# The credential lives in /etc, not /root/.aws: the unit sets ProtectHome=true, which hides /root.
# Defaulted here as well as in the unit so a restore run by hand in an emergency still finds it.
: "${AWS_SHARED_CREDENTIALS_FILE:=/etc/mt-uptime/aws-credentials}"
export AWS_SHARED_CREDENTIALS_FILE
[ -n "${AWS_REGION:-}" ] && export AWS_REGION

LOG_FILE=$BACKUP_DIR/backup.log
TIMESTAMP=$(date -u +%Y%m%dT%H%M%SZ)
NAME="mt-uptime-engine-${TIMESTAMP}"
WORK=$BACKUP_DIR/$NAME

log () { printf '%s  %s\n' "$(date -u +%FT%TZ)" "$*" | tee -a "$LOG_FILE"; }
die () { log "FAILED: $*"; exit 1; }

[ "$(id -u)" -eq 0 ] || { echo "run as root"; exit 1; }
command -v sqlite3 >/dev/null || { echo "sqlite3 is required (apt-get install sqlite3)"; exit 1; }

install -d -m 0700 -o root -g root "$BACKUP_DIR"
touch "$LOG_FILE"; chmod 0600 "$LOG_FILE"

log "=== engine backup $NAME starting ==="
trap 'rc=$?; [ $rc -ne 0 ] && log "aborted with status $rc"; rm -rf "$WORK"' EXIT
mkdir -p "$WORK"

# -- 1. the database, via the online backup API ---------------------------------------------------
[ -f "$DB" ] || die "no database at $DB"
log "copying $DB with the online backup API"
sqlite3 "$DB" ".backup '$WORK/mt-uptime.db'" || die "sqlite3 .backup failed"

DB_BYTES=$(stat -c %s "$WORK/mt-uptime.db")
log "database copy is $DB_BYTES bytes"
[ "$DB_BYTES" -gt 4096 ] || die "the copy is implausibly small ($DB_BYTES bytes)"

# integrity_check reads every page. On a database this size it costs a second and it is the only thing
# that distinguishes "a file was produced" from "a database was produced".
log "verifying the copy"
INTEGRITY=$(sqlite3 "$WORK/mt-uptime.db" "PRAGMA integrity_check;" | head -1)
[ "$INTEGRITY" = "ok" ] || die "integrity_check on the copy said: $INTEGRITY"
log "integrity_check: ok"

# -- 2. the key ring ------------------------------------------------------------------------------
[ -d "$KEYS_DIR" ] || die "no key ring directory at $KEYS_DIR - refusing to take a backup that cannot be restored"
mkdir -p "$WORK/keys"
cp -a "$KEYS_DIR"/*.xml "$WORK/keys/" 2>/dev/null || true

KEY_COUNT=$(find "$WORK/keys" -name 'key-*.xml' | wc -l)
[ "$KEY_COUNT" -gt 0 ] || die "the key ring is empty - a restore from this would lose every stored secret"
log "key ring: $KEY_COUNT key(s)"

# -- 3. THE PAIRING CHECK -------------------------------------------------------------------------
#
# Read the key id out of the ciphertext itself and demand the archive contains that key. See the
# header for why this is the check that matters.
#
# Payload layout: base64url of [09 F0 C9 F0][16-byte key id][...]. The id is written with .NET's
# Guid.ToByteArray(), which is little-endian for the first three groups - hence the byte shuffle.
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

log "checking the key ring against the secrets it has to decrypt"
PAYLOADS=$(
  sqlite3 "$WORK/mt-uptime.db" "SELECT name FROM sqlite_master WHERE type='table';" | while read -r t; do
    sqlite3 "$WORK/mt-uptime.db" "PRAGMA table_info('$t');" | cut -d'|' -f2 | while read -r c; do
      [ -z "$c" ] && continue
      sqlite3 "$WORK/mt-uptime.db" \
        "SELECT CAST(\"$c\" AS TEXT) FROM '$t' WHERE CAST(\"$c\" AS TEXT) LIKE 'CfDJ8%' LIMIT 5;" 2>/dev/null
    done
  done
)

SECRETS=0
UNMATCHED=""
while read -r p; do
  [ -z "$p" ] && continue
  SECRETS=$((SECRETS + 1))
  if kid=$(key_id_of_payload "$p"); then
    [ -f "$WORK/keys/key-$kid.xml" ] || UNMATCHED="$UNMATCHED $kid"
  else
    UNMATCHED="$UNMATCHED (unparseable-payload)"
  fi
done <<< "$PAYLOADS"

if [ "$SECRETS" -eq 0 ]; then
  # Not a failure: a fresh instance with no channels and no mail sender genuinely has no secrets yet.
  log "no encrypted secrets stored yet - nothing to pair (this is normal for a new instance)"
else
  [ -z "$UNMATCHED" ] || die "the key ring does NOT contain the key(s) that encrypted this data:$UNMATCHED - a restore would lose those secrets"
  log "all $SECRETS stored secret(s) are decryptable by the keys in this archive"
fi

# -- 4. configuration -----------------------------------------------------------------------------
if [ -f "$ENV_FILE" ]; then
  cp -a "$ENV_FILE" "$WORK/mt-uptime.env"
  log "included $ENV_FILE"
fi

# -- 5. manifest ----------------------------------------------------------------------------------
{
  echo "MT-Uptime engine backup"
  echo "======================="
  echo "Timestamp (UTC)  $TIMESTAMP"
  echo "Host             $(hostname)"
  echo "Database         $DB"
  echo "Database bytes   $DB_BYTES"
  echo "integrity_check  $INTEGRITY"
  echo "Key ring         $KEY_COUNT key(s), paired against $SECRETS stored secret(s)"
  echo "SQLite           $(sqlite3 --version | cut -d' ' -f1)"
  echo "Keys in archive"
  find "$WORK/keys" -name 'key-*.xml' -printf '  %f\n' | sort
  echo "Rows"
  sqlite3 "$WORK/mt-uptime.db" "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;" | while read -r t; do
    printf '  %s = %s\n' "$t" "$(sqlite3 "$WORK/mt-uptime.db" "SELECT COUNT(*) FROM '$t';" 2>/dev/null || echo '?')"
  done
  echo
  echo "To restore"
  echo "  Rehearse first - it touches nothing:"
  echo "      sudo mt-uptime-engine-restore.sh --rehearse $NAME.tar.gz"
  echo "  Then, only if that passed:"
  echo "      sudo mt-uptime-engine-restore.sh --into $STATE_DIR $NAME.tar.gz --force"
  echo
  echo "THE ARCHIVE CONTAINS THE DATA PROTECTION KEY RING. Anyone holding it can decrypt every secret"
  echo "in the database. It is written 0600 and belongs in storage you would put a password in."
} > "$WORK/MANIFEST.txt"

# -- 6. compress and verify -----------------------------------------------------------------------
cd "$BACKUP_DIR"
tar -czf "$NAME.tar.gz" "$NAME"/
chmod 0600 "$NAME.tar.gz"
FINAL_BYTES=$(stat -c %s "$NAME.tar.gz")
log "archive is $FINAL_BYTES bytes"

gzip -t "$NAME.tar.gz" || die "the archive is not valid gzip"

# LIST ONCE, INTO A VARIABLE, and grep that. `tar | grep -q` looks equivalent and is not: grep exits
# at the first match, tar takes SIGPIPE while it is still writing, and `set -o pipefail` reports the
# whole pipeline as failed. It only shows up once the archive is big enough that tar has not finished
# by the time grep is satisfied - so it passes on a small database and fails on a real one.
LISTING=$(tar -tzf "$NAME.tar.gz") || die "the archive is not readable as a tar"
printf '%s
' "$LISTING" | grep -q "$NAME/mt-uptime.db" || die "the archive does not contain the database"
printf '%s
' "$LISTING" | grep -q "$NAME/keys/key-" || die "the archive does not contain the key ring"
log "archive reads back, with both the database and the key ring in it"

# -- 7. off the box -------------------------------------------------------------------------------
if [ -n "$S3_BUCKET" ]; then
  KEY="$S3_PREFIX/$NAME.tar.gz"
  log "uploading to s3://$S3_BUCKET/$KEY"
  aws s3 cp "$NAME.tar.gz" "s3://$S3_BUCKET/$KEY" --storage-class STANDARD_IA --only-show-errors \
    || die "S3 upload failed - the local copy is on the same volume as the instance, so this run protected nothing"
  REMOTE_BYTES=$(aws s3api head-object --bucket "$S3_BUCKET" --key "$KEY" --query ContentLength --output text)
  [ "$REMOTE_BYTES" = "$FINAL_BYTES" ] || die "S3 object is $REMOTE_BYTES bytes, local is $FINAL_BYTES"
  log "uploaded and size-matched ($REMOTE_BYTES bytes)"
else
  log "!! S3_BUCKET is not set: this backup NEVER LEFT THE MACHINE. It protects you from a bad"
  log "!! migration and not from losing the disk. Set S3_BUCKET, or copy $NAME.tar.gz off the box."
fi

# -- 8. local retention ---------------------------------------------------------------------------
DELETED=$(find "$BACKUP_DIR" -maxdepth 1 -name "mt-uptime-engine-*.tar.gz" -mtime "+$LOCAL_RETENTION_DAYS" -print -delete | wc -l)
log "pruned $DELETED local archive(s) older than $LOCAL_RETENTION_DAYS days"
log "local archives now: $(find "$BACKUP_DIR" -maxdepth 1 -name 'mt-uptime-engine-*.tar.gz' | wc -l)"
log "=== engine backup $NAME complete ==="
