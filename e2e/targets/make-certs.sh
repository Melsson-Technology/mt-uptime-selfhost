#!/usr/bin/env bash
#
# make-certs.sh — mint the certificate set the TLS and database monitor tests need.
#
#   sudo ./make-certs.sh [--dir /etc/mt-uptime-e2e/certs] [--no-trust] [--force]
#
# Produces, under $DIR:
#
#   ca/ca.crt ca/ca.key                 the primary test CA, installed into the system trust store
#   ca-untrusted/ca.crt ...             a second CA that is deliberately NOT trusted
#   web/valid.{crt,key}                 365 days out    — a TLS monitor should report Up
#   web/expiring.{crt,key}              5.5 days out    — Down at WarnDays 14, Up at WarnDays 3
#   web/expired.{crt,key}               ended yesterday — Down, "Certificate expired Nd ago"
#   web/untrusted.{crt,key}             365 days, signed by the second CA
#   mysql/server.{crt,key}              for mysqld's ssl-cert
#   postgres/server.{crt,key}           for postgresql's ssl_cert_file
#   notafter.env                        each leaf's notAfter in ISO-8601, for the manifest
#
# NOTHING HERE MAY EVER BE COMMITTED. scripts/publish-public.sh refuses to publish if a .crt, .key or
# .pem is tracked anywhere inside engine/, which is exactly why these are minted at runtime rather than
# shipped as fixtures. Keep it that way: a test certificate in a public repository is still a private key
# in a public repository.
#
# Why `openssl ca` rather than `openssl x509 -req`: only the CA app lets us set -startdate/-enddate, and
# an expired certificate cannot be produced any other way. OpenSSL 3.0.13 (what Ubuntu 24.04 ships) has
# no `x509 -not_after`, so this is not a stylistic choice.

set -euo pipefail

DIR="/etc/mt-uptime-e2e/certs"
INSTALL_TRUST=1
FORCE=0

for arg in "$@"; do
    case "$arg" in
        --dir=*)     DIR="${arg#--dir=}" ;;
        --no-trust)  INSTALL_TRUST=0 ;;
        --force)     FORCE=1 ;;
        -h|--help)   sed -n '2,30p' "$0"; exit 0 ;;
        *) echo "unknown option: $arg" >&2; exit 1 ;;
    esac
done

# The names every leaf answers to. The monitors all target loopback, but a VerifyFull database
# connection checks the name it dialled against the SAN — so `localhost`, `127.0.0.1` and `::1` all have
# to be in here or "TLS works" and "TLS verifies" stop agreeing.
SAN="DNS:localhost,DNS:e2e.test,IP:127.0.0.1,IP:127.0.0.2,IP:0:0:0:0:0:0:0:1"

CA_DIR="$DIR/ca"
CA2_DIR="$DIR/ca-untrusted"
TRUST_ANCHOR="/usr/local/share/ca-certificates/mt-uptime-e2e-ca.crt"

# ---------------------------------------------------------------------------------------------------
# Is the existing set still fit for purpose?
#
# Re-minting on every run would restart nginx, mysqld and postgres every time this script is invoked,
# which defeats the point of the installer being idempotent. But the expiring/expired certificates are
# defined by their distance from *now*, so a set minted a week ago is no longer the set the tests
# describe — "expiring in 5 days" has silently become "expired". So: keep what is there while it still
# means what it claims, and regenerate the whole set the moment any leaf drifts out of its window.
# ---------------------------------------------------------------------------------------------------
needs_regen() {
    [[ $FORCE -eq 1 ]] && { echo "    --force"; return 0; }

    local f
    for f in "$CA_DIR/ca.crt" "$CA_DIR/ca.key" "$CA2_DIR/ca.crt" "$CA2_DIR/ca.key" \
             "$DIR/web/valid.crt" "$DIR/web/expiring.crt" "$DIR/web/expired.crt" \
             "$DIR/web/untrusted.crt" "$DIR/mysql/server.crt" "$DIR/postgres/server.crt" \
             "$DIR/notafter.env"; do
        [[ -f "$f" ]] || { echo "    $f is missing"; return 0; }
    done

    # `valid` must still be comfortably valid; the tests assert "Valid for ~364d".
    if ! openssl x509 -in "$DIR/web/valid.crt" -noout -checkend 25920000 >/dev/null 2>&1; then
        echo "    web/valid.crt is inside 300 days"; return 0
    fi
    # `expiring` must be inside the 14-day warn window but outside a 3-day one, or the pair of
    # predictions built on it (Down at WarnDays 14, Up at WarnDays 3) stops being a real test.
    if openssl x509 -in "$DIR/web/expiring.crt" -noout -checkend 1209600 >/dev/null 2>&1; then
        echo "    web/expiring.crt is more than 14 days out"; return 0
    fi
    if ! openssl x509 -in "$DIR/web/expiring.crt" -noout -checkend 345600 >/dev/null 2>&1; then
        echo "    web/expiring.crt is inside 4 days"; return 0
    fi
    # `expired` must actually be expired.
    if openssl x509 -in "$DIR/web/expired.crt" -noout -checkend 0 >/dev/null 2>&1; then
        echo "    web/expired.crt has not expired"; return 0
    fi

    return 1
}

echo "==> certificates in $DIR"
if REASON="$(needs_regen)"; then
    [[ -n "$REASON" ]] && echo "$REASON"
else
    echo "    the existing set is still within its windows — left alone (use --force to remint)"
    exit 0
fi

# ---------------------------------------------------------------------------------------------------
# Build the new set beside the old one and swap it in at the end, rather than deleting first.
#
# This started as `rm -rf "$DIR"; mkdir -p ...`, and an interrupted run — a SIGPIPE from piping this
# script's output into `head`, in the case that found it — left the box with NO certificates at all.
# nginx then fails to start, mysqld comes up with TLS silently off, and the next run of the installer
# has to be trusted to notice. An installer whose failure mode is "worse than before it ran" is not
# idempotent in any useful sense, so: mint into a staging directory, and make the destructive step a
# single rename.
# ---------------------------------------------------------------------------------------------------
umask 077

# Sweep anything a previous run left behind. The EXIT trap below covers every ordinary failure, but
# not SIGKILL — and an OOM kill or a `kill -9` during the minute of openssl work would otherwise
# leave a staging directory per attempt, each holding a CA private key.
find "$(dirname "$DIR")" -maxdepth 1 -type d \
    \( -name "$(basename "$DIR").staging.*" -o -name "$(basename "$DIR").previous.*" \) \
    -exec rm -rf {} + 2>/dev/null || true

STAGE="$DIR.staging.$$"
rm -rf "$STAGE"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE"/{web,mysql,postgres}

# Everything below writes to the staging directory; the final block moves it into place.
FINAL_DIR="$DIR"
DIR="$STAGE"
CA_DIR="$DIR/ca"
CA2_DIR="$DIR/ca-untrusted"

# ---------------------------------------------------------------------------------------------------
# The two CAs.
#
# basicConstraints=CA:TRUE is not optional decoration. .NET's X509Chain rejects a signer without it
# outright, so a CA minted with `openssl req -x509` and no extensions produces a chain that curl accepts
# and every VerifyCa database monitor refuses — which reads as a checker bug and is not one.
#
# The second CA exists only so `web/untrusted` has a real issuer that the system store does not know.
# Its CN carries the word UNTRUSTED because the installer's self-check greps for it: if the two CAs were
# ever swapped, every "untrusted" prediction would pass against a trusted certificate and nobody would
# notice.
# ---------------------------------------------------------------------------------------------------
mint_ca() {
    local dir="$1" cn="$2"
    mkdir -p "$dir/newcerts"
    : > "$dir/index.txt"
    echo 01 > "$dir/serial"

    openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 3650 \
        -keyout "$dir/ca.key" -out "$dir/ca.crt" \
        -subj "/O=MT-Uptime E2E/CN=$cn" \
        -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
    chmod 600 "$dir/ca.key"
    chmod 644 "$dir/ca.crt"
}

echo "    minting the two certificate authorities"
mint_ca "$CA_DIR"  "MT-Uptime E2E Test CA"
mint_ca "$CA2_DIR" "MT-Uptime E2E UNTRUSTED CA"

# ---------------------------------------------------------------------------------------------------
# The signing config.
#
#   unique_subject = no     every leaf here is CN=localhost; without this the second one is refused
#   rand_serial    = yes    the serial file is still required to exist, but is not the source of truth
#   copy_extensions = none  a CSR must never be able to talk its way into extensions we did not choose;
#                           the SANs come from the -extensions block below, which we control
# ---------------------------------------------------------------------------------------------------
CONF="$DIR/openssl-ca.cnf"
cat > "$CONF" <<EOF
[ ca ]
default_ca = e2e

[ e2e ]
dir             = \$ENV::CA_DIR
database        = \$dir/index.txt
new_certs_dir   = \$dir/newcerts
certificate     = \$dir/ca.crt
private_key     = \$dir/ca.key
serial          = \$dir/serial
default_md      = sha256
policy          = policy_any
email_in_dn     = no
unique_subject  = no
rand_serial     = yes
copy_extensions = none
preserve        = no

[ policy_any ]
commonName             = supplied
organizationName       = optional
organizationalUnitName = optional
countryName            = optional
stateOrProvinceName    = optional
localityName           = optional
emailAddress           = optional

[ v3_server ]
basicConstraints       = critical,CA:FALSE
keyUsage               = critical,digitalSignature,keyEncipherment
extendedKeyUsage       = serverAuth
subjectKeyIdentifier   = hash
authorityKeyIdentifier = keyid,issuer
subjectAltName         = $SAN
EOF

# stamp <offset-spec> — an OpenSSL ASN1 UTCTime/GeneralizedTime for a moment relative to now.
stamp() { date -u -d "$1" +%Y%m%d%H%M%SZ; }

# mint_leaf <ca-dir> <out-prefix> <startdate-spec> <enddate-spec>
mint_leaf() {
    local ca="$1" out="$2" start="$3" end="$4"
    local csr; csr="$(mktemp)"

    openssl req -newkey rsa:2048 -nodes -keyout "$out.key" -out "$csr" \
        -subj "/O=MT-Uptime E2E/CN=localhost" 2>/dev/null

    CA_DIR="$ca" openssl ca -batch -config "$CONF" -notext -extensions v3_server \
        -startdate "$(stamp "$start")" -enddate "$(stamp "$end")" \
        -in "$csr" -out "$out.crt" 2>/dev/null

    rm -f "$csr"
    chmod 600 "$out.key"
    chmod 644 "$out.crt"
}

echo "    minting leaf certificates"
# Backdated by a day so a box whose clock is slightly behind the signer still sees them as current.
mint_leaf "$CA_DIR"  "$DIR/web/valid"      "-1 day"   "+365 days"
# 5 days 12 hours: the TLS checker floors (notAfter - now) to whole days, so this reads as 5d for the
# first half-day and 4d after. Both sit inside the 14-day warn window and outside a 3-day one, which is
# the only property the tests depend on — they read the exact notAfter out of the manifest rather than
# hardcoding a number that would rot overnight.
mint_leaf "$CA_DIR"  "$DIR/web/expiring"   "-1 day"   "+5 days 12 hours"
mint_leaf "$CA_DIR"  "$DIR/web/expired"    "-10 days" "-2 days"
mint_leaf "$CA2_DIR" "$DIR/web/untrusted"  "-1 day"   "+365 days"
mint_leaf "$CA_DIR"  "$DIR/mysql/server"   "-1 day"   "+365 days"
mint_leaf "$CA_DIR"  "$DIR/postgres/server" "-1 day"  "+365 days"

# ---------------------------------------------------------------------------------------------------
# Record every leaf's notAfter in ISO-8601 so the tests can assert the exact CertExpiresAt the TLS
# checker persists, and can recompute "expires in Nd" themselves instead of hardcoding a day count that
# is only true on the day the certificates were minted.
# ---------------------------------------------------------------------------------------------------
iso_not_after() {
    local end; end="$(openssl x509 -in "$1" -noout -enddate)"
    date -u -d "${end#notAfter=}" +%Y-%m-%dT%H:%M:%SZ
}

{
    echo "TLS_VALID_NOT_AFTER=$(iso_not_after "$DIR/web/valid.crt")"
    echo "TLS_EXPIRING_NOT_AFTER=$(iso_not_after "$DIR/web/expiring.crt")"
    echo "TLS_EXPIRED_NOT_AFTER=$(iso_not_after "$DIR/web/expired.crt")"
    echo "TLS_UNTRUSTED_NOT_AFTER=$(iso_not_after "$DIR/web/untrusted.crt")"
} > "$DIR/notafter.env"
chmod 644 "$DIR/notafter.env"

# The directories themselves have to be traversable: mysqld and postgres read their key from in here as
# their own users, and AppArmor is not the only thing that can refuse them.
chmod 755 "$DIR" "$DIR/web" "$DIR/mysql" "$DIR/postgres" "$CA_DIR" "$CA2_DIR"

# The swap. Two renames rather than one, because rename(2) cannot replace a non-empty directory — but
# both are cheap metadata operations on the same filesystem, so the window where neither set is in place
# is far shorter than the minute of openssl work above.
OLD="$FINAL_DIR.previous.$$"
mkdir -p "$(dirname "$FINAL_DIR")"
if [[ -d "$FINAL_DIR" ]]; then mv "$FINAL_DIR" "$OLD"; fi
mv "$STAGE" "$FINAL_DIR"
rm -rf "$OLD"
trap - EXIT
DIR="$FINAL_DIR"
CA_DIR="$DIR/ca"

if [[ $INSTALL_TRUST -eq 1 ]]; then
    if [[ $EUID -ne 0 ]]; then
        echo "    WARNING: not root, so the CA was NOT added to the system trust store." >&2
        echo "    Re-run with sudo, or pass --no-trust to silence this." >&2
    else
        # The filename must end in .crt. update-ca-certificates skips anything else in this directory
        # without a word, and the resulting failure — every VerifyCa monitor Down — points at the
        # checker rather than at a file extension.
        echo "    installing the CA into the system trust store"
        install -m 0644 "$CA_DIR/ca.crt" "$TRUST_ANCHOR"
        update-ca-certificates >/dev/null

        # .NET reads /etc/ssl/certs once per process and caches it. Any MT-Uptime already running on
        # this box therefore cannot see this CA until it is restarted — which is why install-targets.sh
        # is documented to run BEFORE the application is deployed.
        if systemctl is-active --quiet mt-uptime 2>/dev/null; then
            echo "    WARNING: mt-uptime is already running and has cached the old trust store." >&2
            echo "    Run: sudo systemctl restart mt-uptime" >&2
        fi
    fi
fi

echo "    done: $(find "$DIR" -name '*.crt' | wc -l) certificates"
