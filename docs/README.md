# Dochub

An organizational RAG document hub: sign in with Google/Microsoft SSO, organize
knowledge as **Organization → Team → Group → Artifact**, pull documents in from
GitHub / SharePoint / Google Drive / Azure DevOps / local files, land them in
Azure Blob storage, and drive indexing through a queue with live notifications.

| Piece | Stack | Location |
|---|---|---|
| Web UI | React 18 + TypeScript + Vite | `web/` |
| API | C# .NET 10 minimal APIs + EF Core | `api/` |
| Database | PostgreSQL 16 | `docker-compose.yml` |
| Blob storage | Azure Blob (Azurite locally) | `docker-compose.yml` |
| Eventing | Azure Service Bus (outbox-backed) | optional, off by default |
| API contract | OpenAPI 3.0 | [`openapi.json`](./openapi.json) |

---

## Running it locally

### 1. Infrastructure

```bash
docker compose up -d          # Postgres on 5432, Azurite blob on 10000
```

Both services are pre-wired into `api/src/Dochub.Api/appsettings.Development.json`
(the standard Azurite `devstoreaccount1` credentials), so no secrets are needed
to get a working dev loop.

### 2. API

```bash
cd api
dotnet run --project src/Dochub.Api
```

Serves on **http://localhost:5080**, opens Swagger UI at `/swagger`. On startup it
applies EF migrations automatically and seeds a demo organization (Acme Insurance,
with Claims / Policy / Underwriting teams) because `Database:Seed` is `true` in
Development. No documents are seeded — those only appear once you upload a source.

### 3. Web

```bash
cd web
npm install
npm run dev
```

Serves on **http://localhost:5173**. Use **Continue as developer** on the sign-in
screen to get in without configuring OAuth apps.

> **Status:** the frontend is mid-rewrite onto React 19 + react-router (Vite 8,
> TypeScript 6, oxlint). Screens are landing incrementally, so `npm run build`
> may fail while the rewrite is in flight. The API, database, and infrastructure
> below are complete and stable.

---

## The upload pipeline

This is the core flow the product is built around. One "source" dropped onto an
artifact becomes one `UploadBatch`, which moves through these stages:

```
Registered → Extracting → Uploading → Stored → Queued → Completed
```

1. **Register** — `POST /api/artifacts/{artifactId}/uploads` writes an `UploadBatch`
   row plus one `UploadedDocument` per file and returns immediately (202-style
   acknowledgement). The UI shows the batch in its Processing list right away.
2. **Extract** — the matching `ISourceExtractor` (GitHub, SharePoint, Google Drive,
   Azure DevOps, local) pulls the documents down.
3. **Upload to blob** — each document is written to the container under a
   deterministic prefix:

   ```
   teams/{teamSlug}/groups/{groupSlug}/artifacts/{artifactSlug}/{yyyyMMddTHHmmssfffZ}/...
   ```

   Content is hashed in the same pass that buffers it, so each row records a
   checksum and byte count without a second read.
4. **Persist status** — `UploadedDocument.BlobPath`, size, and checksum are written
   back, and the batch flips to `Stored`.
5. **Publish** — a `document-uploaded` event is written to an **outbox table** in the
   same transaction, then relayed to Azure Service Bus by `OutboxRelayWorker`. The
   outbox is what makes "row committed" and "event published" agree even if the
   broker is unreachable.
6. **Notify** — a `Notification` row is raised and pushed over SignalR
   (`/hubs/notifications`) so the UI toasts without polling. A second notification
   fires when processing completes.

Clicking **Process** on an artifact (`POST /api/artifacts/{artifactId}/process`)
creates a `ProcessingJob` over the stored documents and runs the same
acknowledge → queue → notify cycle.

### Service Bus

`ServiceBus:Enabled` is `false` by default, because Service Bus has no local
emulator. With it off, the outbox relay drains to an in-process queue and
`IndexingWorker` simulates indexing (`Pipeline:SimulateIndexing`) so the full
UI flow — upload, process, notify, land in the knowledge base — works end to end
on a laptop. Point `ServiceBus:ConnectionString` at a real namespace to switch to
the real broker; no code changes required.

---

## Data model

14 entities, all under `api/src/Dochub.Api/Domain/Entities.cs`:

- **Identity & tenancy** — `User`, `Organization`, `OrganizationMember` (`Owner` /
  `Admin` / `Member`). Organization assignment is owner-controlled, and the right to
  create organizations is a per-user flag.
- **Structure** — `Team` → `Group` → `Artifact`, each slugged for stable blob paths.
  `TeamMember` carries `Lead` / `Member`.
- **Ingestion** — `SourceConnection` (per-source OAuth tokens, encrypted at rest via
  ASP.NET Data Protection), `UploadBatch`, `UploadedDocument`.
- **Processing** — `ProcessingJob`, `ProcessingJobDocument`.
- **Delivery** — `Notification`, `OutboxMessage`.

Schema changes go through EF migrations in `api/src/Dochub.Api/Data/Migrations`.

---

## The API contract

[`openapi.json`](./openapi.json) is the generated OpenAPI 3.0 description of all
22 endpoints and 12 schemas. Regenerate it after changing any endpoint:

```bash
cd api
dotnet build
Database__AutoMigrate=false Database__Seed=false \
  Jwt__SigningKey="spec-generation-only-key-not-used-for-anything-real" \
  ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=dochub;Username=dochub;Password=dochub" \
  dotnet swagger tofile --output ../docs/openapi.json \
  src/Dochub.Api/bin/Debug/net10.0/Dochub.Api.dll v1
```

Disabling migration and seeding lets the spec generate without a running database —
the connection string is parsed but never opened.

Endpoint groups: `auth`, `organizations`, `teams`/`groups`/`artifacts`,
`uploads`, `processing-jobs`, `connections`, `notifications`, plus `/health` and
the `/hubs/notifications` SignalR hub.

---

## Configuration

Defaults live in `appsettings.json`; Development overrides in
`appsettings.Development.json`. Anything secret should come from user-secrets or
environment variables rather than either file.

| Key | Meaning |
|---|---|
| `ConnectionStrings:Postgres` | Database connection. |
| `Jwt:SigningKey` | **Required, ≥32 chars.** The API refuses to start without it. |
| `Sso:GoogleClientId` / `MicrosoftClientId` | SSO validation. Blank = that button explains it isn't configured. |
| `Sso:AllowDevSignIn` | Enables the developer sign-in bypass. Never enable outside local. |
| `AzureStorage:ConnectionString` / `Container` | Blob destination. |
| `ServiceBus:Enabled` / `ConnectionString` | Real broker vs. in-process fallback. |
| `Database:AutoMigrate` / `Seed` | Startup migration and demo seeding. |
| `Pipeline:SimulateIndexing` | Fake the indexing stage so the flow completes locally. |
| `Cors:Origins` | Allowed web origins. |

The web app reads `VITE_API_URL` (defaulting to `http://localhost:5080`),
`VITE_ALLOW_DEV_SIGNIN`, `VITE_GOOGLE_CLIENT_ID`, `VITE_MICROSOFT_CLIENT_ID`, and
`VITE_MICROSOFT_TENANT`. Put them in `web/.env.local`.

---

## Before deploying

- Replace `Jwt:SigningKey` with a real secret and set `Sso:AllowDevSignIn=false`
  (and `VITE_ALLOW_DEV_SIGNIN=false`).
- Point `AzureStorage` and `ServiceBus` at real Azure resources and turn
  `ServiceBus:Enabled` on.
- Turn off `Pipeline:SimulateIndexing` once a real indexer consumes the queue.
- Consider running migrations as a deploy step rather than via `Database:AutoMigrate`.
