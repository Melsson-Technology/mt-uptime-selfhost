#!/bin/bash
# Install the engine's nightly backup - and REFUSE to arm it until a restore has been proven.
#
# The gate is the unusual part and it is the point. "The backup procedure ships and is unrehearsed" has
# been the largest unmitigated risk to this instance since the project began, precisely because
# rehearsing used to mean overwriting the live instance. mt-uptime-engine-restore.sh --rehearse removes
# that excuse, so this makes a passing rehearsal a precondition of arming the timer.
#
# Off-box storage is optional. Without S3_BUCKET the backup still runs and still verifies itself, and
# says loudly that it protects you from a bad migration and not from losing the disk.
set -euo pipefail

SRC=$(cd "$(dirname "$0")" && pwd)
STATE_DIR=${STATE_DIR:-/var/lib/mt-uptime}
BACKUP_DIR=${BACKUP_DIR:-/var/backups/mt-uptime-engine}
S3_BUCKET=${S3_BUCKET:-}

[ "$(id -u)" -eq 0 ] || { echo "!! run as root"; exit 1; }

echo "== 0. prerequisites =="
command -v sqlite3 >/dev/null || {
  echo "!! sqlite3 is required for a consistent copy of a live database:"
  echo "     apt-get install -y sqlite3"
  exit 1
}
echo "   sqlite3: $(sqlite3 --version | cut -d' ' -f1)"

[ -d "$STATE_DIR" ] || { echo "!! no state directory at $STATE_DIR"; exit 1; }
[ -f "$STATE_DIR/mt-uptime.db" ] || { echo "!! no database at $STATE_DIR/mt-uptime.db"; exit 1; }
[ -d "$STATE_DIR/keys" ] || { echo "!! no key ring at $STATE_DIR/keys - refusing to install a backup that cannot restore secrets"; exit 1; }
echo "   state:   $(du -sh "$STATE_DIR" | cut -f1) at $STATE_DIR, key ring present"

# LINE ENDINGS. These are copied from a Windows working tree by scp, which - unlike git - applies no
# normalisation. A CRLF script fails on Linux with a syntax error naming the wrong line. Learned on
# 2026-09-08, when a one-line fix shipped CRLF and broke every backup on the box.
for f in mt-uptime-engine-backup.sh mt-uptime-engine-restore.sh; do
  if [ "$(wc -c < "$SRC/$f")" -ne "$(tr -d '\015' < "$SRC/$f" | wc -c)" ]; then
    echo "!! $f has CRLF line endings - it will not run on Linux"; exit 1
  fi
  bash -n "$SRC/$f" || { echo "!! $f does not parse"; exit 1; }
done
echo "   scripts: LF, and both parse"

if [ -n "$S3_BUCKET" ]; then
  export AWS_SHARED_CREDENTIALS_FILE=${AWS_SHARED_CREDENTIALS_FILE:-/etc/mt-uptime/aws-credentials}
  export AWS_REGION=${AWS_REGION:-}
  [ -n "$AWS_REGION" ] || { echo "!! set AWS_REGION alongside S3_BUCKET"; exit 1; }
  command -v aws >/dev/null || { echo "!! aws cli missing but S3_BUCKET is set"; exit 1; }
  [ -f "$AWS_SHARED_CREDENTIALS_FILE" ] || { echo "!! $AWS_SHARED_CREDENTIALS_FILE missing"; exit 1; }
  mode=$(stat -c %a "$AWS_SHARED_CREDENTIALS_FILE")
  [ "$mode" = "600" ] || { echo "!! $AWS_SHARED_CREDENTIALS_FILE is $mode, must be 600"; exit 1; }
  echo "   s3:      $S3_BUCKET, credential root-only ($mode)"
else
  echo "   s3:      NOT CONFIGURED - backups will stay on this machine"
fi

echo
echo "== 1. install =="
# Site config goes in a file both the unit and a hand-run restore read, so the unit itself names no
# bucket and can ship publicly unchanged.
if [ -n "$S3_BUCKET" ]; then
  install -d -m 0751 -o root -g root /etc/mt-uptime
  umask 077
  {
    echo "# written by install-backups.sh on $(date -u +%FT%TZ)"
    echo "S3_BUCKET=$S3_BUCKET"
    echo "S3_PREFIX=${S3_PREFIX:-engine}"
    echo "AWS_REGION=$AWS_REGION"
  } > /etc/mt-uptime/backup.env
  chmod 0600 /etc/mt-uptime/backup.env
  echo "   wrote /etc/mt-uptime/backup.env"
fi
install -m 0700 -o root -g root "$SRC/mt-uptime-engine-backup.sh"  /usr/local/sbin/mt-uptime-engine-backup.sh
install -m 0700 -o root -g root "$SRC/mt-uptime-engine-restore.sh" /usr/local/sbin/mt-uptime-engine-restore.sh
install -m 0644 -o root -g root "$SRC/mt-uptime-engine-backup.service" /etc/systemd/system/
install -m 0644 -o root -g root "$SRC/mt-uptime-engine-backup.timer"   /etc/systemd/system/
install -d -m 0700 -o root -g root "$BACKUP_DIR"
systemctl daemon-reload
echo "   installed, daemon-reloaded"

echo
echo "== 2. take one backup now, watched, rather than unattended at 03:50 =="
systemctl start mt-uptime-engine-backup.service
result=$(systemctl show -p Result --value mt-uptime-engine-backup.service)
journalctl -u mt-uptime-engine-backup.service -n 40 --no-pager \
  | sed -n 's/.*mt-uptime-engine-backup\.sh\[[0-9]*\]: //p' | sed 's/^/   /'
[ "$result" = "success" ] || { echo "!! the backup run failed ($result) - not arming the timer"; exit 1; }
echo "   result: $result"

echo
echo "== 3. THE GATE: a restore rehearsal must pass before the timer is armed =="
latest=$(find "$BACKUP_DIR" -maxdepth 1 -name 'mt-uptime-engine-*.tar.gz' -printf '%T@ %p\n' | sort -rn | head -1 | cut -d' ' -f2-)
[ -n "$latest" ] || { echo "!! no archive to rehearse"; exit 1; }
echo "   rehearsing $(basename "$latest")"
/usr/local/sbin/mt-uptime-engine-restore.sh --rehearse "$latest" | sed 's/^/   /'

echo
echo "== 4. arm it =="
systemctl enable --now mt-uptime-engine-backup.timer
systemctl list-timers mt-uptime-engine-backup.timer --no-pager | sed -n '2p' | sed 's/^/   /'

echo
echo "== done. Armed AND proven restorable, which is the pair that matters. =="
echo "   Rehearse any time:  sudo /usr/local/sbin/mt-uptime-engine-restore.sh --rehearse <name>.tar.gz"
