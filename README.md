# Dochub

An organizational RAG document hub: sign in with Google or Microsoft, organize documents
into **teams → groups → artifacts**, import them from GitHub, SharePoint, Google Drive,
Azure DevOps or local files, and process them into a shared knowledge base.

```
Dochub App/
├── api/          C# .NET 10 minimal API + EF Core + Azure Blob + Azure Service Bus
├── web/          React 19 + TypeScript + Vite SPA
├── docs/         OpenAPI 3.0 contract
├── UI-design/    the original design bundle this UI was built from
└── docker-compose.yml   Postgres + Azurite (Blob emulator)
```

---

## Running it

### 1. Infrastructure

```bash
docker compose up -d          # Postgres on 5432, Azurite blob on 10000
docker compose --profile tools up -d   # …and pgAdmin on 8080
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
`{"provider":"dev","idToken":"owner@acme-insurance.com"}`, copy the `accessToken`, then
press **Authorize** at the top right and paste it.

Migrations apply automatically on start. In `Development` the demo organization
(Acme Insurance, with the Claims / Policy / Underwriting structure) is seeded too — no
documents, since those only exist once a real source has been uploaded.

### 3. Web

```bash
cd web
cp .env.example .env          # already done if you cloned this working tree
npm install
npm run dev                   # http://localhost:5173
```

Sign in with the **Sign in without SSO** box using `owner@acme-insurance.com` — the seeded
owner. Real SSO takes over as soon as you fill in the client ids (below).

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
   │  Vector service      │  status: Processing → Processed
   │  (Python, separate)  │  embeds and stores vectors
   └──────────────────────┘
```

| Step | What happens | Where |
|---|---|---|
| 1 | Local bytes are streamed to staging — only for local files, only at this moment. | `POST /api/uploads/staging` |
| 2 | A `source_documents` row is written with status **Request Upload** and the source is put on the extract queue. | `SourceSubmissionService` |
| 3 | The extractor claims one message, flips the row to **Uploading**, and moves each file to `teams/{team}/groups/{group}/artifacts/{artifact}/{yyyyMMddTHHmmssfffZ}/…`. | `ExtractorService` + `ExtractorWorker` |
| 4 | Every file stored, status **Uploaded**, and the document batch goes onto the process queue. | `ExtractorService` |
| 5 | The vector service consumes that queue, embeds the documents and stores the vectors. Status **Processing** → **Processed**. | Python service (not built yet) |

The `source_documents` row is the single unit of work: its status is the whole answer
to "where has my upload got to", and the Processing list shows exactly these rows.

### The extractor takes one message at a time

Deliberately sequential. A single large repository should slow the queue down rather
than exhaust connections and blob throughput for everything behind it, and a failure
is always attributable to one source. It shares only the database and the queue with
the API, so `Extractor:Enabled=false` here plus `true` in a second deployment moves it
out of this process with nothing else to change.

### What the Python service will receive

`dochub-document-process-requested` carries container, path, checksum and revision per
document — never the bytes, never a token. The consumer fetches from blob storage with
its own credentials.

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
      "checksumSha256": "7e64abe0…", "revision": 1
    }
  ]
}
```

Its job is to write the vectors and move each document to `Indexed` and the source to
`Processed`. Until it exists, `VectorService:SimulateLocally=true` stands in for it —
it consumes the same queue and advances the same statuses, but produces no embeddings.
**Turn that off the moment the real service runs**, or the two will compete for messages.

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

### Notifications### Notifications

Toasts, the bell badge and job progress arrive over SignalR at `/hubs/notifications`.
Clients join a group keyed by the `org_id` claim in their token, so a client cannot
subscribe to another tenant. Everything is also persisted, so `GET /api/notifications`
is a complete fallback if the socket drops.

---

## Database

PostgreSQL, schema `dochub`, 14 tables. The full DDL lives in
`api/src/Dochub.Api/Data/Migrations/`.

```
users ──┬── organization_members ──── organizations
        │                                  │
        └── team_members ──── teams ───────┘
                                │
                             groups
                                │
                            artifacts ──── source_documents ──── uploaded_documents

source_connections   notifications   queue_messages   recurring_sync_schedules
```

Points worth knowing:

- **`source_documents`** is the table written when Process is pressed. It references the
  artifact and carries the lifecycle `Request Upload → Uploading → Uploaded → Processing
  → Processed`, the blob container and prefix, and the per-run tallies.
- **`uploaded_documents`** is one row per file inside a source, with `blob_path`,
  `blob_url`, a SHA-256 and a revision counter.
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

## Configuration

Everything below is in `api/src/Dochub.Api/appsettings.json`, overridable by environment
variables (`Jwt__SigningKey`, `AzureStorage__ConnectionString`, …) or user-secrets.

| Key | Purpose |
|---|---|
| `ConnectionStrings:Postgres` | Database. Defaults to the docker-compose instance. |
| `Jwt:SigningKey` | **Required**, ≥32 chars. The app refuses to start without it. |
| `Sso:GoogleClientId` / `Sso:MicrosoftClientId` | Sign-in to Dochub. ID tokens are verified server-side against Google's and Microsoft's JWKS before any claim is trusted. |
| `OAuth:Google` / `OAuth:Microsoft` | Access to someone's **documents** — a separate grant from signing in. See "Connecting Google Drive and SharePoint". |
| `Sso:AllowDevSignIn` | Local-only escape hatch that accepts an unverified email. Keep this `false` everywhere else. |
| `AzureStorage:ConnectionString` | Azurite locally; a real connection string, or `AccountUrl` for managed identity, in Azure. |
| `ServiceBus:Enabled` | `false` routes both queues to Postgres. Set `true` with a connection string or namespace to use a real broker. |
| `ServiceBus:ExtractQueue` / `ProcessQueue` | Queue names. Defaults `dochub-source-upload-requested` and `dochub-document-process-requested`. |
| `Extractor:Enabled` | Runs the extractor in this process. See "The extractor takes one message at a time". |
| `VectorService:SimulateLocally` | Stands in for the Python service. **Must be `false` once that service is running.** |
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

#### Common errors

| Error | Cause |
|---|---|
| `AADSTS900971: No reply address provided` | `OAuth:RedirectUri` is blank. The parameter is then dropped from the request entirely — nothing to do with the portal. The API now refuses to start in this state and names the setting. |
| `AADSTS50011: redirect URI does not match` | The registered URI differs from `OAuth:RedirectUri`. They must match **exactly** — scheme, host, port, path, trailing slash. `GET /api/connections/oauth/{source}/start` echoes `redirectUri` so you can compare them side by side. |
| `AADSTS7000218` | Web platform without a client secret. See the table above. |
| `AADSTS9002327` | Registered as SPA. Re-register the redirect URI under Web. |
| `503 not_configured` | No client id set, or the redirect URI is invalid. The message names the setting. |

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
3. Set `ServiceBus:Enabled=true` and `VectorService:SimulateLocally=false`.
4. Point the Python vector service at `dochub-document-process-requested`, and have it
   update `uploaded_documents.status` and `source_documents.status` as it works.

---

## Checking it works

```bash
./scripts/smoke-test.sh
```

Eleven checks: the service responds, Postgres and Azurite are reachable, dev sign-in
issues a token, an anonymous call is rejected, and a real file goes all the way from
staging through Process to `Processed`. It names the fix when something is down.

## Tests

```bash
cd api && dotnet test
```

65 tests. The cadence maths is covered as pure functions (including both DST edges and
the February case). The pipeline and the sync reconciliation run against a throwaway
Postgres database created per test class — the schema leans on jsonb, partial indexes,
a sequence and SKIP LOCKED, none of which an in-memory provider models.
`docker compose up -d` must be running.

## API contract

`docs/openapi.yaml` is the source of truth and can be imported straight into Postman.
The running API also serves a generated document at `/swagger/v1/swagger.json`.

---

## What isn't built

- **The vector service.** Step 5 is not built. The process queue is populated and its
  contract is fixed, but embedding and vector storage belong to the separate Python
  service. `VectorService:SimulateLocally` stands in for it locally.
- **Retrieval.** The Ask screen shows the corpus and says plainly that querying isn't
  wired up; it depends on the vector service above.
- **Confluence and Jira extractors.** Both are selectable and their connections store
  correctly, but registering an upload records the batch and reports the unsupported
  source on the status row rather than importing anything.
- **Azure DevOps** imports wiki pages only, not repository files. It signs in through
  Microsoft like SharePoint, but is still pointed at by organization/project/wiki rather
  than by a link.
- **Confluence and Jira** still take a pasted token; neither has a consent flow yet.
- **Deletions are not propagated.** A file removed from a synced folder keeps its
  document and its vectors; only additions and changes are acted on.
- **Superseded blobs are kept.** A re-synced document points at its newest blob and the
  previous one stays in storage as history, with no lifecycle rule to expire it yet.
