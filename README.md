# Dochub

An organizational RAG document hub: sign in with Google or Microsoft, organize documents
into **teams → groups → artifacts**, import them from GitHub, SharePoint, Google Drive,
Azure DevOps or local files, and process them into a shared knowledge base.

```
Dochub App/
├── api/          C# .NET 10 minimal API + EF Core + Azure Blob + Azure Service Bus
├── web/          React 19 + TypeScript + Vite SPA
├── ragplatform/  Python RAG ingestion service — consumes the process queue (see its README)
├── docs/         OpenAPI 3.0 contract
├── UI-design/    the original design bundle this UI was built from
└── docker-compose.yml   Postgres + Azurite (Blob emulator) + pgvector for the RAG platform
```

---

## Running it

### 1. Infrastructure

```bash
docker compose up -d                 # Postgres 5432, Azurite 10000, rag-postgres (pgvector) 5433
docker compose --profile rag up -d   # …and the RAG worker + search API (8090) in containers
```

> On macOS the Docker CLI may not be on your PATH. Either open Docker Desktop and wait for
> the engine to report *Running*, or add `/Applications/Docker.app/Contents/Resources/bin`
> to your PATH.

### 2. API

```bash
cd api/src/Dochub.Api         # dotnet run only works from HERE
dotnet run
```

`dotnet run` needs the project folder — from the repo root or from `api/` it fails with
*"Couldn't find a project to run"*, because `api/` holds a solution with two projects.
From anywhere else, name the project instead:

```bash
dotnet run --project api/src/Dochub.Api
```

A healthy start prints where everything is:

```
Now listening on: http://localhost:5080
Dochub API ready — Swagger UI at http://localhost:5080/swagger, …
```

| URL | What it is |
|---|---|
| `http://localhost:5080` | Redirects to Swagger |
| `http://localhost:5080/swagger` | Swagger UI — every endpoint, callable in the browser |
| `http://localhost:5080/swagger/v1/swagger.json` | The generated OpenAPI document |
| `http://localhost:5080/health` | Liveness plus a real database check |

To call a secured endpoint from Swagger: run `POST /api/auth/sso` with
`{"provider":"dev","idToken":"you@example.com"}` (development only), copy the
`accessToken`, then press **Authorize** at the top right and paste it.

Migrations apply automatically on start. **Nothing is seeded** — no demo organization,
no sample documents. Every organization is created by a real user with the right to do
so (see "Who can create an organization"), and everything under it by its members.

### 3. Web

```bash
cd web
cp .env.example .env          # already done if you cloned this working tree
npm install
npm run dev                   # http://localhost:5173
```

Sign in with Google or Microsoft. The **Sign in without SSO** box (development only)
accepts any email; use one listed in `Database:Administrators` to be able to create the
first organization.

### 4. RAG platform

```bash
cd ragplatform && . .venv/bin/activate
rag-ingest worker     # consumes the process queue
rag-ingest api        # search on http://localhost:8090
```

Setup, configuration and operation are in [`ragplatform/README.md`](ragplatform/README.md).

---

## The pipeline

Nothing reaches the backend while you are choosing sources. The browser holds the
repository details, the folder ids and the local `File` objects until **Process** is
pressed — that is the only call the Upload screen makes.

```
        Process pressed
              │
              ▼
   ┌──────────────────────┐
   │  source_documents    │  status: Request Upload
   │  (references the     │
   │   artifact)          │
   └──────────┬───────────┘
              │  queue 1: dochub-source-upload-requested
              ▼
   ┌──────────────────────┐
   │  Extractor service   │  status: Uploading
   │  one message at a    │  moves files → Azure Blob storage
   │  time                │  status: Uploaded
   └──────────┬───────────┘
              │  queue 2: dochub-document-process-requested
              ▼
   ┌──────────────────────┐
   │  RAG platform        │  status: Processing → Processed
   │  (ragplatform/)      │  chunks, embeds, indexes; calls back
   └──────────────────────┘  per document and per artifact
```

| Step | What happens | Where |
|---|---|---|
| 1 | Local bytes are streamed to staging — only for local files, only at this moment. | `POST /api/uploads/staging` |
| 2 | A `source_documents` row is written with status **Request Upload** and the source is put on the extract queue. | `SourceSubmissionService` |
| 3 | The extractor claims one message, flips the row to **Uploading**, and moves each file to `teams/{team}/groups/{group}/artifacts/{artifact}/{yyyyMMddTHHmmssfffZ}/…`. | `ExtractorService` + `ExtractorWorker` |
| 4 | For each blob, the **content MD5 Azure returns** and its ETag are written to a new `document_versions` row. Every file stored, status **Uploaded**, and the document batch goes onto the process queue. | `ExtractorService` |
| 5 | The RAG platform consumes that queue, indexes each document, and calls `POST /api/ingestion/documents/{id}/processed` (or `/failed`) for each. Status **Processing**. | `ragplatform/` |
| 6 | When the batch is done it calls `POST /api/ingestion/artifacts/{id}/processed`; the API closes the source (**Processed**, **Partially failed** or **Failed**) and sets the artifact's status. | `IngestionEndpoints` |

The `source_documents` row is the single unit of work: its status is the whole answer
to "where has my upload got to", and the Processing list shows exactly these rows.

### The extractor takes one message at a time

Deliberately sequential. A single large repository should slow the queue down rather
than exhaust connections and blob throughput for everything behind it, and a failure
is always attributable to one source. It shares only the database and the queue with
the API, so `Extractor:Enabled=false` here plus `true` in a second deployment moves it
out of this process with nothing else to change.

### What the RAG platform receives

`dochub-document-process-requested` carries container, path, hashes and revision per
document — never the bytes, never a token. The consumer fetches from blob storage with
its own credentials and checks the bytes against `contentMd5`.

```json
{
  "sourceDocumentId": "8690e96b-…", "reference": "SRC-1000",
  "artifactId": "a9dc19f7-…", "organizationId": "80c997f0-…",
  "artifactName": "UW Manuals", "teamName": "Underwriting", "groupName": "Guidelines",
  "sourceType": "Local", "blobContainer": "dochub-documents",
  "blobPrefix": "teams/underwriting/groups/guidelines/artifacts/uw-manuals/20260927T042853391Z",
  "documentCount": 2, "uploadedAt": "2026-09-27T04:28:55Z",
  "documents": [
    {
      "documentId": "79d66252-…", "name": "uw-manual.md",
      "relativePath": "manuals/uw-manual.md",
      "blobPath": "teams/…/20260927T042853391Z/manuals/uw-manual.md",
      "blobUrl": "https://…", "sizeBytes": 46,
      "contentType": "application/octet-stream",
      "checksumSha256": "7e64abe0…", "contentMd5": "Cc1DVWbsE3AuUE4jDeOrGQ==",
      "revision": 1, "documentVersionId": "3f0c…",
      "sourceType": "Local", "externalId": null
    }
  ]
}
```

There is no simulator: if the RAG worker is not running, sources stop at **Uploaded**
and the messages wait on the queue until it starts.

### Ingestion callbacks

The RAG platform reports back over HTTP, not over a queue, so the API can answer —
in particular `409 stale_version` when a newer upload of the same file has already
replaced the version being reported.

| Call | Effect |
|---|---|
| `POST /api/ingestion/documents/{id}/processed` | Document → **Indexed**, with chunk count and processed time. First one moves the source to **Processing**. |
| `POST /api/ingestion/documents/{id}/failed` | Document → **Failed** with the stage and error, shown in the UI. |
| `POST /api/ingestion/artifacts/{id}/processed` | Closes the source batch. Anything still unreported is marked failed, and the artifact status is computed from **all** its documents: `Processed`, `PartiallyProcessed` or `Failed`. Raises the completion notification. |

These are service-to-service calls: anonymous to JWT, but every request must carry
`X-Dochub-Service-Key` equal to `Ingestion:ServiceKey` (compared in constant time). The
endpoints answer `503` until that key is configured with at least 32 characters.

### Removing a failed upload

- **Automatic:** if an upload to Blob storage fails partway, the files it had already
  stored are deleted and its version rows removed; documents it had updated go back to
  their previous version. Nothing had been handed to processing, so nothing else needs undoing.
- **Remove** (Processing list, for *Failed*, *Completed with errors* or *Cancelled*
  uploads; by whoever started it, or an owner/admin): queued work for it is withdrawn
  (refused while a step is running), its documents leave the search index
  (`POST /admin/purge` on the RAG platform), its Blob folder is deleted, and every row it
  wrote goes in one transaction. Documents it only *updated* return to their previous
  version and are re-indexed if the index had already moved on. If a step fails the
  upload stays **Removing** and pressing Remove again carries on.

### Recurring updates use the content hash

SharePoint, Google Drive and **GitHub** locations can be kept in sync. A public GitHub
repository needs no connection; GitHub files are matched by their path in the repository
(a repository has many `README.md`s), the others by file name. A file whose last
processing failed is always processed again, even if its bytes haven't changed.

A scheduled re-sync downloads each file again and uploads it to a new timestamped
prefix. The MD5 Azure returns is compared with the one stored for the same file name:
equal means unchanged — the new blob is discarded, no version is written and nothing is
sent for processing. Different means a new `document_versions` row (revision + 1) and
only that file goes to the process queue.

### Failure handling

A message that throws is held invisible for an exponential backoff — 15s, then a
minute, then four — capped at an hour, and dead-lettered after five attempts. Most
failures here are an expired token, which needs time to be fixed rather than an
instant retry. On a real broker the same spacing is achieved with a scheduled
redelivery, since `AbandonMessage` alone redelivers immediately.

A failed source shows a **Retry** button in the Processing list, which puts it back on
the extract queue with a fresh message id.

### Queues without Azure

`ServiceBus:Enabled=false` routes both queues to a Postgres-backed queue with the same
semantics — durable messages, visibility timeouts, delivery counts, dead-lettering and
one-at-a-time claims via `SELECT … FOR UPDATE SKIP LOCKED`. The consuming code is
identical either way; only the `IQueueClient` registration changes.

### Chat

The **Chat** tab answers questions from the indexed documents, with numbered,
clickable sources (document, section, and page, slide, sheet rows or time range).
**Ask** hands its question to Chat.

```
browser ── POST /api/chat/conversations/{id}/messages ──► Dochub API ── POST /chat ──► RAG platform
        ◄────────────── text/event-stream ──────────────          ◄── text/event-stream ──
```

- **Conversations** are per person, per organization (`chat_conversations`,
  `chat_messages`), listed newest first. Each turn is saved with the sources it was
  written from, the source numbers it cites, the model and token usage — also when the
  stream fails or the user stops it, so the record shows what was said.
- **Scope**: the whole organization, a team, a group or one artifact, changeable at any
  time. The API resolves it to artifact ids on every turn (so documents added to a team
  later are included) and refuses a team or artifact from another organization.
- **Tenancy is decided by the API**, never the browser: the RAG platform receives the
  organization from the user's token as its tenant and `org:{id}` as the principal, and
  only accepts calls carrying `Ingestion:ServiceKey`.
- **Ranking is two-stage:** keyword and vector search are fused, then a reranker
  re-scores the candidates — NVIDIA Nemotron rerank (free, via OpenRouter) for now,
  Cohere Rerank 3.5 later (`RERANK_PROVIDER`). Measured with `rag-ingest eval`:
  MRR 0.826 → 0.958 on the evaluation set.
- **Answers need an LLM** on the RAG platform (`LLM_PROVIDER`). Without one, Chat still
  works and shows the most relevant passages with a note that no model is configured.

| Endpoint | |
|---|---|
| `GET /api/chat/conversations` | The caller's conversations |
| `POST /api/chat/conversations` | `{scope: Organization\|Team\|Group\|Artifact, scopeId}` |
| `GET /api/chat/conversations/{id}` | With messages and their sources |
| `PATCH /api/chat/conversations/{id}` | Rename, or change scope |
| `DELETE /api/chat/conversations/{id}` | |
| `POST /api/chat/conversations/{id}/messages` | `{content}` → SSE: `started`, `sources`, `delta`…, `done` (or `error`) |

### Notifications

Each person can clear one notification (×) or all of them. Clearing hides them for that
person only (`notification_dismissals`); others still see them.

Toasts, the bell badge and job progress arrive over SignalR at `/hubs/notifications`.
Clients join a group keyed by the `org_id` claim in their token, so a client cannot
subscribe to another tenant. Everything is also persisted, so `GET /api/notifications`
is a complete fallback if the socket drops.

---

## Database

PostgreSQL, schema `dochub`, 16 tables. The full DDL lives in
`api/src/Dochub.Api/Data/Migrations/`.

```
users ──┬── organization_members ──── organizations
        │                                  │
        └── team_members ──── teams ───────┘
                                │
                             groups
                                │
                            artifacts ──── source_documents ──── uploaded_documents
                                                                        │
                                                                document_versions

source_connections   notifications   queue_messages   recurring_sync_schedules
chat_conversations ──── chat_messages
```

Points worth knowing:

- **`source_documents`** is the table written when Process is pressed. It references the
  artifact and carries the lifecycle `Request Upload → Uploading → Uploaded → Processing
  → Processed`, the blob container and prefix, and the per-run tallies.
- **`uploaded_documents`** is one row per file inside a source, with `blob_path`,
  `blob_url`, a SHA-256, the Azure content MD5, a revision counter, and once indexed the
  chunk count and processed time.
- **`document_versions`** is one row per stored version of a file: name, path, source,
  size, blob location, ETag, last-modified, and the **content MD5 returned by Azure** plus
  our SHA-256. `uploaded_documents.current_version_id` points at the latest. This is the
  history recurring updates compare against.
- **`artifacts.status`** is `Empty → Pending → Processing → Processed` (or
  `PartiallyProcessed` / `Failed`), set by the API from its documents.
- **`queue_messages`** backs the local queue when no Service Bus namespace is configured.
- **`artifact_id` is denormalized** onto `uploaded_documents` so artifact-scoped reads
  stay a single-table scan rather than a join through the batch.
- **Re-imports are idempotent**: a unique index on `(artifact_id, source_type, external_id)`
  means re-running a GitHub or Drive import updates rows instead of duplicating them.
- **Source references come from a Postgres sequence**, so two people pressing Process at
  the same moment can never be handed the same `SRC-` number.
- **Tokens are encrypted at rest** with ASP.NET Data Protection and are never returned by
  the API.
- **`recurring_sync_schedules`** is the reference table behind recurring updates: one row
  per artifact+source holding frequency, time of day, time zone, `next_run_at`, last-run
  counters and a claim flag. A partial index on `(status, next_run_at)` keeps the
  worker's only hot query cheap.

---

## Who can create an organization

Any one of these is enough:

- `users.can_create_organizations` — a platform-level flag
- `organization_members.can_create_organizations` — a per-membership grant in **any** org
- **Owner or Admin** in **any** organization

Anyone else gets `403 insufficient_level`. Whoever creates an organization becomes its
**Owner**, and Owner is the only level that can add members — Admins cannot.

### The Creator role

`Platform:Creators` (in `appsettings.json`; currently `bhavik.can89@gmail.com`) lists the
people who set organizations up for others. A Creator can create an organization and
name its **owner by login id** — the address they sign in with: a Microsoft work or
school account, a personal Microsoft account, or a Google account — without becoming a
member themselves. From **Organizations** (`/platform`) they can add or remove owners of
any organization; an organization always keeps at least one owner. Someone named before
they have ever signed in gets the role the first time they sign in with that address.
The list is re-applied on every start, and taking an address out revokes the role.

### Bootstrapping the first account

A brand-new user who belongs to no organization fails all three checks, so the first
account has to be granted explicitly:

```jsonc
"Database": {
  "Administrators": [ "someone@example.com" ]
}
```

Re-applied on **every** start, not just on a fresh database, so the grant survives a
`docker compose down -v`. For each address it ensures the user exists, sets the
create-organizations right, and makes them an **Admin of every organization**.

It only ever adds access. An Owner listed here stays an Owner — the grant never demotes
anyone and never removes a membership.

If the person has not signed in yet, a placeholder user is created that binds to their
real provider identity the first time they do, exactly as an owner's invite does.

---

## Configuration

Everything below is in `api/src/Dochub.Api/appsettings.json`, overridable by environment
variables (`Jwt__SigningKey`, `AzureStorage__ConnectionString`, …) or user-secrets.

| Key | Purpose |
|---|---|
| `ConnectionStrings:Postgres` | Database. Defaults to the docker-compose instance. |
| `Jwt:SigningKey` | **Required**, ≥32 chars. The app refuses to start without it. |
| `Sso:GoogleClientId` / `Sso:MicrosoftClientId` | Sign-in to Dochub. ID tokens are verified server-side against Google's and Microsoft's JWKS before any claim is trusted. **Leave empty to reuse the matching `OAuth:…:ClientId`** — the usual case, where one registration both signs people in and reads their documents. |
| `Database:Administrators` | Emails granted Admin plus the create-organizations right on every start. See "Who can create an organization". |
| `OAuth:Google` / `OAuth:Microsoft` | Access to someone's **documents** — a separate grant from signing in. See "Connecting Google Drive and SharePoint". |
| `Sso:AllowDevSignIn` | Local-only escape hatch that accepts an unverified email. Keep this `false` everywhere else. |
| `AzureStorage:ConnectionString` | Azurite locally; a real connection string, or `AccountUrl` for managed identity, in Azure. |
| `ServiceBus:Enabled` | `false` routes both queues to Postgres. Set `true` with a connection string or namespace to use a real broker. |
| `ServiceBus:ExtractQueue` / `ProcessQueue` | Queue names. Defaults `dochub-source-upload-requested` and `dochub-document-process-requested`. |
| `Extractor:Enabled` | Runs the extractor in this process. See "The extractor takes one message at a time". |
| `Ingestion:ServiceKey` | Shared secret between the API and the RAG platform, both directions: its callbacks to us and our search/chat calls to it (≥32 chars). Same value as `DOCHUB_SERVICE_KEY` in `ragplatform/.env`. |
| `Rag:BaseUrl` | The RAG platform's API (`http://localhost:8090`). `Rag:TimeoutSeconds` (180) bounds one answer; `Rag:HistoryMessages` (12) is how many earlier messages go with a question. |
| `Sync:Enabled` | Runs the recurring-sync worker in this process. See "Running the sync service separately". |
| `Sync:PollIntervalSeconds` | How often to look for due schedules. Default 60. |
| `Sync:MaxPerSweep` | Ceiling on schedules started per sweep, so a backlog drains steadily. |

The web app reads `VITE_API_URL`, `VITE_GOOGLE_CLIENT_ID`, `VITE_MICROSOFT_CLIENT_ID`,
`VITE_MICROSOFT_TENANT` and `VITE_ALLOW_DEV_SIGNIN` from `web/.env`.

## Connecting Google Drive and SharePoint

These two sources are pointed at by a **link**, and reaching what is behind that link
needs the user's own permission. Both are handled by a real consent flow rather than
by asking someone to find an access token:

1. **Sign in.** "Sign in with Google" / "Sign in with Microsoft" opens the provider's
   consent screen in a **popup** — a full-page redirect would discard the sources the
   Upload screen is holding in memory.
2. **Paste the link.** Open the folder in Drive or SharePoint, copy the address, paste
   it. Dochub resolves it to the ids the connector needs and confirms the signed-in
   account can actually read it, so a permissions problem surfaces now rather than
   halfway through an import.
3. **Process.** Unchanged from any other source.

Authorization Code with **PKCE**, and the code is exchanged **server-side** so the
refresh token never reaches the browser. That refresh token matters: an access token
lasts about an hour, so without it a schedule set up today would quietly stop working
tomorrow. `SourceTokenProvider` renews five minutes before expiry, and only asks the
user to reconnect when the provider actually refuses — revoked access, changed
password, admin removal.

The OAuth `state` is encrypted with Data Protection and carries the PKCE verifier, the
organization and the user. Nothing is stored server-side, and a callback replayed into
another user's session is rejected because the state says who started the flow.

### Setting it up

Register an OAuth app with each provider and set the redirect URI to
`http://localhost:5173/oauth/callback` (whatever `OAuth:RedirectUri` says — it points
at the **web app**, not the API).

| Provider | Scopes to request |
|---|---|
| Google | `openid email https://www.googleapis.com/auth/drive.readonly` |
| Microsoft | `openid email offline_access Files.Read.All Sites.Read.All` |

```jsonc
"OAuth": {
  "RedirectUri": "http://localhost:5173/oauth/callback",
  "Google":    { "ClientId": "…apps.googleusercontent.com", "ClientSecret": "…" },
  "Microsoft": { "ClientId": "…", "ClientSecret": "…", "Tenant": "common" }
}
```

#### Microsoft Entra: pick the right platform

Dochub exchanges the code **server-side**, which decides the registration:

| Platform in Entra | Works? |
|---|---|
| **Web** + a client secret | **Yes — use this.** |
| Web, no secret | No. `AADSTS7000218` — the exchange needs a secret. |
| Single-page application (SPA) | No. `AADSTS9002327` — SPA tokens may only be redeemed cross-origin, and our exchange is server-to-server. |
| Mobile and desktop (public client) | Yes, if you cannot use a secret. |

So **Web platform plus `OAuth:Microsoft:ClientSecret`** is the combination to register.
Google is happier either way; supply the secret if its registration is a Web application.

#### Checking what is actually sent

```bash
./scripts/check-oauth.sh SharePoint     # or GoogleDrive
```

It prints the reply address in brackets, so trailing whitespace and a stray slash are
visible, and flags the differences you cannot see by eye. The API also logs the same
string at startup.

#### Common errors

| Error | Cause |
|---|---|
| `AADSTS900971: No reply address provided` | `OAuth:RedirectUri` is blank. The parameter is then dropped from the request entirely — nothing to do with the portal. The API now refuses to start in this state and names the setting. |
| `The provided value for the input parameter 'redirect_uri' is not valid` (`AADSTS50011`) | Either the strings differ, or — far more often when they look identical — the URI is registered under the **wrong platform**. See below. |
| `AADSTS7000218` | Web platform without a client secret. See the table above. |
| `AADSTS9002327` | Registered as SPA. Re-register the redirect URI under Web. |
| `503 not_configured` | No client id set, or the redirect URI is invalid. The message names the setting. |

#### Both Microsoft buttons now share one callback

There are two places this app talks to Microsoft:

| Button | What it does | Client id |
|---|---|---|
| **Continue with Microsoft** (sign-in screen) | Proves who you are | `VITE_MICROSOFT_CLIENT_ID` |
| **Sign in with Microsoft** (Add a source) | Grants read access to documents | `OAuth:Microsoft:ClientId` |

Both return to **`http://localhost:5173/oauth/callback`**. They used to use different
paths (`/auth/callback` and `/oauth/callback`), which meant registering the wrong one
produced a redirect-URI error on whichever button you happened to press. One path now
serves both — the callback tells them apart by what comes back, an `id_token` in the
fragment for sign-in and a `code` in the query for a source. `/auth/callback` still
resolves, so an existing portal entry keeps working.

If the two buttons use **different app registrations**, register that one URI in both.

#### When the redirect URI "looks identical" but is still rejected

Entra keeps redirect URIs in **separate lists per platform**. A URI added under
*Single-page application* lives in a different collection from one under *Web*, and a
server-side code exchange only matches the Web list — so the portal shows the string you
expect while the request still fails.

Fix: in **App registrations → Authentication**, delete the entry under
*Single-page application* and add it under **Web** instead. One URI, one platform; having
it in both is not required and the SPA copy is the one that misleads.

Then check, in order:

1. **Trailing slash.** `…/oauth/callback` and `…/oauth/callback/` are different URIs.
2. **Host.** `localhost` and `127.0.0.1` are different URIs.
3. **Port.** The reply address points at the **web app** (5173), not the API (5080) —
   a common slip, since every other URL in the setup is the API's.
4. **Case.** The path is case-sensitive: `/oauth/callback` ≠ `/OAuth/Callback`.
5. **Whitespace.** A trailing space pasted into the portal is invisible there but counts.
6. **The client id.** `./scripts/check-oauth.sh` prints the client id being sent. If two
   app registrations exist, the URI may be sitting in the other one.

#### "The provided value for the input parameter 'redirect_uri' is not valid"

That exact wording comes from the **consumer** identity system, not the usual
`AADSTS50011` from Entra — a sign that the request reached the personal-Microsoft-account
endpoint. `Tenant: "common"` allows that. If the app is only for your organization, set
`OAuth:Microsoft:Tenant` (and `VITE_MICROSOFT_TENANT`) to your tenant id, or to
`organizations`, and the request never goes near it.

Until a client id is set the API answers `503 not_configured` and names the exact
setting, rather than failing somewhere inside the flow. `POST /api/connections` is still
available as an escape hatch for a token obtained elsewhere, but a connection made that
way has no refresh token and will expire.

### CORS

In **development** the API accepts any loopback origin — `localhost` or `127.0.0.1`, on
any port. This is deliberate: Vite moves to the next free port when 5173 is taken, and
a fixed allowlist turns that into an unexplained CORS failure rather than an obvious
"something else is already running". The dev server is pinned with `strictPort` so it
fails loudly instead of drifting, and the two together mean this should not bite again.

Loopback means the parsed host, not a string match, so `localhost.evil.com`,
`127.0.0.1.evil.com` and `http://evil.com/#localhost` are all rejected. `CorsOriginTests`
covers those.

Outside development only `Cors:Origins` is trusted, and the API **refuses to start** if
that list is empty — an empty allowlist would silently block every browser client.

If you do hit a CORS error, check in this order:

1. **Which port is the UI actually on?** The Vite banner says. If it is not 5173,
   something else is holding the port.
2. **Is `VITE_API_URL` right?** It must match where the API is listening (`http://localhost:5080`).
3. **Is the API running at all?** A connection refused shows up in the browser as a
   CORS error too. `./scripts/smoke-test.sh` distinguishes them.

### Going to real Azure

1. Create a storage account and a Service Bus namespace with two queues,
   `dochub-source-upload-requested` and `dochub-document-process-requested`.
2. Set `AzureStorage:AccountUrl` and `ServiceBus:Namespace`, and grant the app's managed
   identity *Storage Blob Data Contributor* and *Azure Service Bus Data Sender*.
   `DefaultAzureCredential` picks it up — no secrets in config.
3. Set `ServiceBus:Enabled=true` and a strong `Ingestion:ServiceKey`.
4. Run the RAG platform with `QUEUE_BACKEND=servicebus`, the same key as
   `DOCHUB_SERVICE_KEY`, and `DOCHUB_API_URL` pointing at the API.

---

## Checking it works

```bash
./scripts/smoke-test.sh
```

Fourteen checks: the service responds, Postgres and Azurite are reachable, dev sign-in
issues a token, an anonymous call is rejected, a real file goes all the way from
staging through Process and the RAG worker to `Processed`, the RAG search API finds
it, and a chat question streams sources and an answer. It names the fix when something
is down.

It signs in as `SMOKE_EMAIL` (default: the first `Database:Administrators` entry), and
since nothing is seeded that account needs an artifact to upload into — create one in
the UI first.

## Tests

```bash
cd api && dotnet test
```

80 tests. The cadence maths is covered as pure functions (including both DST edges and
the February case). The pipeline and the sync reconciliation run against a throwaway
Postgres database created per test class — the schema leans on jsonb, partial indexes,
a sequence and SKIP LOCKED, none of which an in-memory provider models.
`docker compose up -d` must be running.

The RAG platform has its own 89 (`cd ragplatform && pytest`).

## API contract

`docs/openapi.yaml` is the source of truth and can be imported straight into Postman.
The running API also serves a generated document at `/swagger/v1/swagger.json`.

---

## What isn't built

- **Legacy Office formats.** The RAG platform (phase 4 of its spec) indexes text,
  Markdown, notebooks, code, PDF, Word, PowerPoint, HTML, images with OCR, XLSX/CSV, and
  audio/video transcripts. Legacy `.doc`/`.ppt`/`.xls`/`.rtf` are refused with advice
  to re-save them as `.docx`/`.pptx`/`.xlsx`. Speaker diarization is not built.
- **Chat needs an LLM on the RAG platform.** Locally it uses Azure OpenAI (`gpt-5.5`
  for answers and enrichment, `text-embedding-3-small` for vectors); without
  `LLM_PROVIDER` set, Chat returns passages without written answers.
- **Confluence and Jira extractors.** Both are selectable and their connections store
  correctly, but registering an upload records the batch and reports the unsupported
  source on the status row rather than importing anything.
- **Azure DevOps** imports wiki pages only, not repository files. It signs in through
  Microsoft like SharePoint, but is still pointed at by organization/project/wiki rather
  than by a link.
- **Confluence and Jira** still take a pasted token; neither has a consent flow yet.
- **Superseded blobs are kept.** A re-synced document points at its newest blob and the
  previous one stays in storage as history, with no lifecycle rule to expire it yet.
