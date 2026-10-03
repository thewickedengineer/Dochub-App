#!/usr/bin/env bash
# Shows exactly what Dochub sends to the provider, so it can be compared with
# what is registered in the portal.
#   ./scripts/check-oauth.sh [SharePoint|GoogleDrive] [base-url]
set -uo pipefail

SOURCE="${1:-SharePoint}"
BASE="${2:-http://localhost:5080}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Any account will do — this only reads the authorize URL. Defaults to the first admin.
EMAIL="${SMOKE_EMAIL:-$(python3 -c 'import json,sys; print((json.load(open(sys.argv[1])).get("Database",{}).get("Administrators") or ["oauth-check@example.com"])[0])' \
  "$ROOT/api/src/Dochub.Api/appsettings.Development.json" 2>/dev/null)}"

TOKEN=$(curl -s -X POST "$BASE/api/auth/sso" -H 'Content-Type: application/json' \
  -d "{\"provider\":\"dev\",\"idToken\":\"$EMAIL\"}" \
  | python3 -c 'import sys,json; print(json.load(sys.stdin).get("accessToken",""))' 2>/dev/null)

if [ -z "$TOKEN" ]; then
  echo "Could not sign in. Is the API running?  cd api/src/Dochub.Api && dotnet run"
  exit 1
fi

RESPONSE=$(curl -s "$BASE/api/connections/oauth/$SOURCE/start" -H "Authorization: Bearer $TOKEN")

# Passed as an argument, not on stdin — the interpreter script is the heredoc.
python3 - "$SOURCE" "$RESPONSE" <<'PY'
import json, sys, urllib.parse as u

source, body = sys.argv[1], sys.argv[2]

try:
    data = json.loads(body)
except json.JSONDecodeError:
    print(f"Unexpected response from the API:\n{body[:400]}")
    raise SystemExit(1)

if "authorizeUrl" not in data:
    print(f"  {data.get('code', 'error')}: {data.get('message', data)}")
    raise SystemExit(1)

query = u.parse_qs(u.urlparse(data["authorizeUrl"]).query)
redirect = query.get("redirect_uri", [""])[0]

endpoint = u.urlparse(data["authorizeUrl"])
tenant = endpoint.path.split("/")[1] if data["provider"] == "Microsoft" else "-"

print(f"Source      {source}")
print(f"Provider    {data['provider']}")
print(f"Client id   {query.get('client_id', [''])[0]}")
print(f"Tenant      {tenant}")
print(f"Endpoint    {endpoint.scheme}://{endpoint.netloc}{endpoint.path}")
print(f"Scopes      {query.get('scope', [''])[0]}")
print()
print("Reply address actually sent — register EXACTLY this string:")
print()
print(f"    [{redirect}]")
print()

# The differences that cause a mismatch are the ones you cannot see.
problems = []
if redirect != redirect.strip():
    problems.append("it has leading or trailing whitespace")
if redirect.endswith("/"):
    problems.append("it ends with a slash — the portal entry must too, or remove it here")
if "127.0.0.1" in redirect:
    problems.append("it uses 127.0.0.1; 'localhost' is a different URI to the provider")
if redirect.startswith("http://") and "localhost" not in redirect:
    problems.append("it is plain http on a non-localhost host, which providers reject")

if problems:
    print("Worth checking:")
    for p in problems:
        print(f"  - {p}")
    print()

print("Compare character by character with the portal. Scheme, host, port, path and")
print("trailing slash must all match, and the path is case-sensitive.")

if data["provider"] == "Microsoft":
    print()
    print("In Entra, check WHICH platform the URI sits under. A URI added under")
    print("'Single-page application' is stored separately from 'Web' and will not match")
    print("this request, even though both look identical in the portal.")
    print("Dochub exchanges the code server-side, so it must be under Web.")

    if tenant == "common":
        print()
        print("Tenant is 'common', which lets personal Microsoft accounts in. Those are")
        print("validated by the consumer identity system, which is stricter about reply")
        print("addresses and reports exactly this error. If the app is only for your")
        print("organization, set OAuth:Microsoft:Tenant to your tenant id (or")
        print("'organizations') and the request stops going near the consumer endpoint.")

print()
print("Open this in a browser to see the provider's own error page — it names the")
print("application it resolved the client id to, which settles whether the URI was")
print("added to the app the request is actually using:")
print()
print(data["authorizeUrl"])
PY
