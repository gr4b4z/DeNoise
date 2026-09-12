# Alert Hub

Alert Hub ingests alerts from Azure Monitor, MongoDB Atlas and generic webhooks, deduplicates them into
episodes, assigns ownership, auto-resolves on Hub-configured policies, verifies monitoring coverage with
heartbeats and canaries, and delivers notifications through templated, signed outbound webhooks and SMTP.

Single deployable modular application: .NET 10 backend (three hosts: `ingest`, `api`, `workers`),
React 19 frontend, PostgreSQL only, runs on AKS.

## Where to start

| Need | Read |
|---|---|
| How the agent/engineer works in this repo | [`AGENTS.md`](AGENTS.md) |
| Behavioural source of truth | [`docs/spec/alert-hub-spec-v0.4.md`](docs/spec/alert-hub-spec-v0.4.md) |
| Architecture and decision records | [`docs/03-architecture-and-adrs.md`](docs/03-architecture-and-adrs.md) |
| Order of work (milestones) | [`docs/09-implementation-plan.md`](docs/09-implementation-plan.md) |
| Everything else in the handoff pack | [`docs/00-README.md`](docs/00-README.md) |

## Commands

```bash
# backend
dotnet build
dotnet test --filter Category=Unit          # fast path
dotnet test                                 # unit + integration (PostgreSQL required)
dotnet run --project src/AlertHub.Api

# frontend
cd web && pnpm install && pnpm dev

# local stack
docker compose up -d postgres mailpit
```

Integration tests use Testcontainers by default. Set `ALERTHUB_TEST_CONNECTION` to a PostgreSQL
connection string to run them against an existing server instead (used in environments without Docker).

## Operations

- Hub health screen: `/hub` (component heartbeats, queues, outbox lag, failure queue, retention). The hub must never be the only monitor of itself — see `docs/runbooks/hub-failure.md` for the independent signals and the response.
- Retention runs daily at 02:00 UTC on the scheduler role (`Retention:*`, Helm `retention.*`); `POST /api/v1/hub/retention/run` runs it on demand.
- Load test: `k6 run -e INGEST_URL=… -e TOKEN=… -e BASELINE_RPS=<C9> build/loadtest/ingest-k6.js`.
- Backup/restore drill: `build/backup-restore-drill.sh <database>` (quarterly; keep the output).

## Status

Milestone progress is tracked in [`CHANGELOG.md`](CHANGELOG.md) and in the milestone READMEs under `docs/`.
