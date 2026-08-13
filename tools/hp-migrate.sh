#!/usr/bin/env bash
# One-shot migration: promote existing Standard-tier L2 objects to
# OVH High Performance (EXPRESS_ONEZONE) tier via S3 CopyObject.
#
# Runs on Server B. Reads encrypted L2 creds from Postgres, decrypts
# with the same AES-GCM scheme ConfigEncryptionService uses, then
# sweeps all objects under segments/ prefix via copy-in-place.
#
# Usage: bash hp-migrate.sh

set -euo pipefail

# --- dependencies ---
command -v python3 >/dev/null 2>&1 || { echo "python3 required" >&2; exit 1; }
command -v aws     >/dev/null 2>&1 || { echo "aws-cli required (sudo apt install awscli)" >&2; exit 1; }
python3 -c 'import cryptography' 2>/dev/null || { echo "python3-cryptography required (sudo apt install python3-cryptography)" >&2; exit 1; }
command -v docker  >/dev/null 2>&1 || { echo "docker required (psql is invoked via docker exec nzbdav-postgres)" >&2; exit 1; }

# --- fetch encrypted ciphertexts + plain bucket/endpoint from DB ---
PGCMD='docker exec nzbdav-postgres psql -U nzbdav -d nzbdav -tAc'

ACCESS_CT=$($PGCMD "SELECT \"ConfigValue\" FROM \"ConfigItems\" WHERE \"ConfigName\"='cache.l2.access-key'")
SECRET_CT=$($PGCMD "SELECT \"ConfigValue\" FROM \"ConfigItems\" WHERE \"ConfigName\"='cache.l2.secret-key'")
BUCKET=$($PGCMD "SELECT \"ConfigValue\" FROM \"ConfigItems\" WHERE \"ConfigName\"='cache.l2.bucket-name'")
ENDPOINT=$($PGCMD "SELECT \"ConfigValue\" FROM \"ConfigItems\" WHERE \"ConfigName\"='cache.l2.endpoint'")
SSL=$($PGCMD "SELECT \"ConfigValue\" FROM \"ConfigItems\" WHERE \"ConfigName\"='cache.l2.ssl'")

SCHEME=$([ "${SSL,,}" = "true" ] && echo "https" || echo "http")
ENDPOINT_URL="${SCHEME}://${ENDPOINT}"

# --- decrypt with master key (AES-256-GCM matching ConfigEncryptionService) ---
: "${NZBDAV_MASTER_KEY:?Set NZBDAV_MASTER_KEY to decrypt L2 creds}"

decrypt() {
    local ciphertext="$1"
    NZBDAV_MASTER_KEY="$NZBDAV_MASTER_KEY" CIPHERTEXT="$ciphertext" python3 <<'PY'
import base64, os, sys
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
key = base64.b64decode(os.environ["NZBDAV_MASTER_KEY"])
ct = os.environ["CIPHERTEXT"]
if not ct.startswith("v1:"):
    sys.stderr.write("Missing v1: prefix\n"); sys.exit(1)
b64 = ct[3:].replace("-", "+").replace("_", "/")
b64 += "=" * (-len(b64) % 4)
packed = base64.b64decode(b64)
nonce, body_tag = packed[:12], packed[12:]
plaintext = AESGCM(key).decrypt(nonce, body_tag, None)
sys.stdout.write(plaintext.decode())
PY
}

export AWS_ACCESS_KEY_ID=$(decrypt "$ACCESS_CT")
export AWS_SECRET_ACCESS_KEY=$(decrypt "$SECRET_CT")
export AWS_DEFAULT_REGION=us-east-va

echo "=== L2 target ==="
echo "bucket:   $BUCKET"
echo "endpoint: $ENDPOINT_URL"
echo "region:   $AWS_DEFAULT_REGION"
echo "key id:   ${AWS_ACCESS_KEY_ID:0:6}...$(echo -n "$AWS_ACCESS_KEY_ID" | tail -c 4)"
echo

# --- list keys, then copy-in-place with storage-class change ---
echo "=== listing current Standard-tier objects ==="
aws s3api list-objects-v2 \
    --bucket "$BUCKET" \
    --prefix "segments/" \
    --endpoint-url "$ENDPOINT_URL" \
    --query 'Contents[].Key' --output text > /tmp/hp-keys.txt

COUNT=$(wc -l < /tmp/hp-keys.txt)
echo "objects to migrate: $COUNT"

if [ "$COUNT" -eq 0 ]; then
    echo "nothing to migrate"; exit 0
fi

echo
echo "=== copy-in-place to EXPRESS_ONEZONE (concurrency=16) ==="
tr '\t' '\n' < /tmp/hp-keys.txt | \
xargs -P 16 -I{} bash -c '
    aws s3api copy-object \
        --bucket "$0" \
        --key "$1" \
        --copy-source "$0/$1" \
        --storage-class EXPRESS_ONEZONE \
        --metadata-directive COPY \
        --endpoint-url "$2" \
        >/dev/null && echo "ok $1" || echo "FAIL $1"
' "$BUCKET" {} "$ENDPOINT_URL" | tee /tmp/hp-migrate.log | grep -c ok

echo
echo "=== done ==="
OK=$(grep -c '^ok ' /tmp/hp-migrate.log || true)
FAIL=$(grep -c '^FAIL ' /tmp/hp-migrate.log || true)
echo "ok: $OK"
echo "fail: $FAIL"
[ "$FAIL" -gt 0 ] && echo "check /tmp/hp-migrate.log for fails"
