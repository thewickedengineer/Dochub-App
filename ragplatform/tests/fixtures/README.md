# Claims Core

Claims Core receives first notice of loss (FNOL) and drives the claim lifecycle.

## Service levels

First notice of loss must be acknowledged within 24 hours for personal lines and
within 2 business days for commercial lines. Catastrophe events follow the CAT
playbook instead.

| Line       | Acknowledge within | Owner          |
|------------|--------------------|----------------|
| Personal   | 24 hours           | Claims intake  |
| Commercial | 2 business days    | Commercial ops |

## Running locally

1. Start the database.
2. Run the migrations.
3. Start the API.

```bash
docker compose up -d
dotnet run --project src/Claims.Api
```

## Escalation

Unacknowledged claims escalate to the duty manager after the SLA lapses.
