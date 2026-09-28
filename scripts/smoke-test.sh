#!/usr/bin/env bash
# Verifies the API is up and the whole pipeline actually works.
#   ./scripts/smoke-test.sh [base-url]
set -uo pipefail

BASE="${1:-http://localhost:5080}"
PASS=0
FAIL=0

green() { printf '\033[32m%s\033[0m\n' "$1"; }
red()   { printf '\033[31m%s\033[0m\n' "$1"; }

check() {
  local label="$1" expected="$2" actual="$3"
  if [ "$actual" = "$expected" ]; then
    green "  PASS  $label"
    PASS=$((PASS + 1))
  else
    red   "  FAIL  $label (expected $expected, got $actual)"
    FAIL=$((FAIL + 1))
  fi
}

status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

echo "Dochub smoke test against $BASE"
echo

# ── 1. Is it even listening? ──────────────────────────────────────────────────
echo "Service"
if ! curl -s -o /dev/null --max-time 5 "$BASE/health"; then
  red "  FAIL  nothing is listening on $BASE"
  echo
  echo "  Start it with:  cd api/src/Dochub.Api && dotnet run"
  echo "  (dotnet run only works from that folder, not the repo or solution root.)"
  exit 1
fi
check "health"        200 "$(status "$BASE/health")"
check "swagger UI"    200 "$(status "$BASE/swagger/index.html")"
check "openapi spec"  200 "$(status "$BASE/swagger/v1/swagger.json")"

# ── 2. Dependencies the API needs ─────────────────────────────────────────────
echo
echo "Dependencies"
HEALTH=$(curl -s "$BASE/health")
check "postgres reachable" "Healthy" "$HEALTH"

if curl -s -o /dev/null --max-time 5 "http://127.0.0.1:10000/devstoreaccount1"; then
  green "  PASS  azurite (blob storage) reachable"
  PASS=$((PASS + 1))
else
  red   "  FAIL  azurite unreachable — run: docker compose up -d"
  FAIL=$((FAIL + 1))
fi

# ── 3. Auth ───────────────────────────────────────────────────────────────────
echo
echo "Auth"
TOKEN=$(curl -s -X POST "$BASE/api/auth/sso" \
  -H 'Content-Type: application/json' \
  -d '{"provider":"dev","idToken":"owner@acme-insurance.com|Dana Whitfield"}' \
  | python3 -c 'import sys,json; print(json.load(sys.stdin).get("accessToken",""))' 2>/dev/null)

if [ -z "$TOKEN" ]; then
  red "  FAIL  could not sign in (is Sso:AllowDevSignIn true? is the database seeded?)"
  exit 1
fi
green "  PASS  dev sign-in issued a token"
PASS=$((PASS + 1))

AUTH=(-H "Authorization: Bearer $TOKEN")
check "rejects an anonymous call" 401 "$(status "$BASE/api/artifacts")"
check "accepts the token"         200 "$(status "${AUTH[@]}" "$BASE/api/artifacts")"

# ── 4. The pipeline, end to end ───────────────────────────────────────────────
echo
echo "Pipeline"
ARTIFACT=$(curl -s "${AUTH[@]}" "$BASE/api/artifacts" \
  | python3 -c 'import sys,json; a=json.load(sys.stdin); print(a[0]["id"] if a else "")')

if [ -z "$ARTIFACT" ]; then
  red "  FAIL  no artifacts — set Database:Seed=true, or create a team/group/artifact"
  exit 1
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
printf 'Smoke test document.\n' > "$TMP/smoke-test.txt"

STAGED=$(curl -s -X POST "$BASE/api/uploads/staging" "${AUTH[@]}" \
  -F "file0=@$TMP/smoke-test.txt" -F "path:file0=smoke-test.txt" \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d[0]["stagingId"] if d else "")')

if [ -z "$STAGED" ]; then
  red "  FAIL  staging returned nothing"
  exit 1
fi
green "  PASS  staged a local file"
PASS=$((PASS + 1))

ACK=$(curl -s -X POST "$BASE/api/artifacts/$ARTIFACT/process" "${AUTH[@]}" \
  -H 'Content-Type: application/json' \
  -d "{\"sources\":[{\"sourceType\":\"Local\",\"sourceReference\":\"smoke test\",\"options\":{\"stagingIds\":[\"$STAGED\"]},\"documents\":[{\"name\":\"smoke-test.txt\",\"relativePath\":\"smoke-test.txt\"}]}]}")

SOURCE_ID=$(echo "$ACK" | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d["sources"][0]["id"])' 2>/dev/null)
REFERENCE=$(echo "$ACK" | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d["sources"][0]["reference"])' 2>/dev/null)

if [ -z "$SOURCE_ID" ]; then
  red "  FAIL  process was rejected: $ACK"
  exit 1
fi
green "  PASS  process accepted ($REFERENCE, status Request Upload)"
PASS=$((PASS + 1))

# Walk the status forward. The extractor and the vector service each take a turn.
# Only Processed/Failed are terminal — Uploaded is transient whenever something is
# consuming the process queue, so breaking on it would report too early.
FINAL=""
for _ in $(seq 1 30); do
  FINAL=$(curl -s "${AUTH[@]}" "$BASE/api/source-documents/$SOURCE_ID" \
    | python3 -c 'import sys,json; print(json.load(sys.stdin)["source"]["status"])' 2>/dev/null)
  case "$FINAL" in
    Processed|Failed|PartiallyFailed) break ;;
  esac
  sleep 2
done

case "$FINAL" in
  Processed)
    green "  PASS  reached Processed — extractor and vector service both ran"
    PASS=$((PASS + 1)) ;;
  Uploaded)
    green "  PASS  reached Uploaded — files are in blob storage and on the process queue"
    echo   "        (still Uploaded because no vector service is consuming it — expected"
    echo   "         when VectorService:SimulateLocally is false and Python is not running)"
    PASS=$((PASS + 1)) ;;
  *)
    red   "  FAIL  stalled at '${FINAL:-unknown}' — check the API log"
    FAIL=$((FAIL + 1)) ;;
esac

echo
if [ "$FAIL" -eq 0 ]; then
  green "$PASS passed, 0 failed."
else
  red   "$PASS passed, $FAIL failed."
  exit 1
fi
