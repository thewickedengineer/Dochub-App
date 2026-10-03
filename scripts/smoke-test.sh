#!/usr/bin/env bash
# Verifies the API is up and the whole pipeline actually works.
#   ./scripts/smoke-test.sh [base-url]
#
# Signs in (dev sign-in) as SMOKE_EMAIL, defaulting to the first address in
# Database:Administrators. Nothing is seeded any more, so that account must belong to
# an organization with at least one artifact — create one in the UI first.
set -uo pipefail

BASE="${1:-http://localhost:5080}"
RAG="${RAG_URL:-http://localhost:8090}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SMOKE_EMAIL="${SMOKE_EMAIL:-$(python3 -c 'import json,sys; print((json.load(open(sys.argv[1])).get("Database",{}).get("Administrators") or [""])[0])' \
  "$ROOT/api/src/Dochub.Api/appsettings.Development.json" 2>/dev/null)}"
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
if [ -z "$SMOKE_EMAIL" ]; then
  red "  FAIL  no account to sign in as — set SMOKE_EMAIL or Database:Administrators"
  exit 1
fi
TOKEN=$(curl -s -X POST "$BASE/api/auth/sso" \
  -H 'Content-Type: application/json' \
  -d "{\"provider\":\"dev\",\"idToken\":\"$SMOKE_EMAIL\"}" \
  | python3 -c 'import sys,json; print(json.load(sys.stdin).get("accessToken",""))' 2>/dev/null)

if [ -z "$TOKEN" ]; then
  red "  FAIL  could not sign in as $SMOKE_EMAIL (is Sso:AllowDevSignIn true?)"
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
  red "  FAIL  $SMOKE_EMAIL sees no artifacts — create an organization, team, group and"
  red "        artifact in the UI first (nothing is seeded)"
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
    green "  PASS  reached Processed — extractor and RAG worker both ran"
    PASS=$((PASS + 1)) ;;
  Uploaded)
    red   "  FAIL  stuck at Uploaded — files are in blob storage and on the process queue,"
    red   "        but no RAG worker is consuming it. Start it: cd ragplatform && rag-ingest worker"
    FAIL=$((FAIL + 1)) ;;
  *)
    red   "  FAIL  stalled at '${FINAL:-unknown}' — check the API log"
    FAIL=$((FAIL + 1)) ;;
esac

# ── 5. The RAG platform ───────────────────────────────────────────────────────
echo
echo "RAG platform"
if [ "$(status --max-time 5 "$RAG/health")" = "200" ]; then
  green "  PASS  rag-ingest api healthy at $RAG"
  PASS=$((PASS + 1))
  # The organization is the tenant; membership of it is the principal the adapter grants.
  ORG=$(python3 -c 'import sys,json,base64; p=sys.argv[1].split(".")[1]; print(json.loads(base64.urlsafe_b64decode(p+"="*(-len(p)%4)))["org_id"])' "$TOKEN")
  # Search is service-to-service: it needs the key the API and the RAG platform share.
  SERVICE_KEY="${SERVICE_KEY:-$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("Ingestion",{}).get("ServiceKey",""))' \
    "$ROOT/api/src/Dochub.Api/appsettings.Development.json" 2>/dev/null)}"
  HITS=$(curl -s -X POST "$RAG/search" -H 'Content-Type: application/json' -H "X-Dochub-Service-Key: $SERVICE_KEY" \
    -d "{\"tenant_id\":\"$ORG\",\"principals\":[\"org:$ORG\"],\"query\":\"Smoke test document\",\"top_k\":3,\"filters\":{\"artifact_id\":\"$ARTIFACT\"}}" \
    | python3 -c 'import sys,json; print(len(json.load(sys.stdin)))' 2>/dev/null)
  check "search finds the smoke document" "yes" "$([ "${HITS:-0}" -gt 0 ] && echo yes || echo no)"
else
  red   "  FAIL  rag-ingest api not answering at $RAG — cd ragplatform && rag-ingest api"
  FAIL=$((FAIL + 1))
fi

# ── 6. Chat through the API ──────────────────────────────────────────────────
echo
echo "Chat"
CONV=$(curl -s -X POST "$BASE/api/chat/conversations" "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d "{\"scope\":\"Artifact\",\"scopeId\":\"$ARTIFACT\"}" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("id",""))' 2>/dev/null)
if [ -z "$CONV" ]; then
  red "  FAIL  could not start a conversation"
  FAIL=$((FAIL + 1))
else
  EVENTS=$(curl -sN -X POST "$BASE/api/chat/conversations/$CONV/messages" "${AUTH[@]}" \
    -H 'Content-Type: application/json' -d '{"content":"Smoke test document"}' \
    | python3 -c 'import sys,json,itertools; t=[json.loads(l[6:])["type"] for l in sys.stdin if l.startswith("data: ")]; print(" ".join(k for k,_ in itertools.groupby(t)))')
  check "chat streams sources and an answer" "started sources delta done" "$EVENTS"
  curl -s -o /dev/null -X DELETE "$BASE/api/chat/conversations/$CONV" "${AUTH[@]}"
fi

echo
if [ "$FAIL" -eq 0 ]; then
  green "$PASS passed, 0 failed."
else
  red   "$PASS passed, $FAIL failed."
  exit 1
fi
