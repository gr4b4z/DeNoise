# Milestone 0 — Skeleton

Status: **done**. No owner input required (09).

## What exists

| Area | Where | Notes |
|---|---|---|
| Solution layout | `AlertHub.sln`, `src/`, `tests/` | Layout from AGENTS.md §3. `Domain` has no package references. Central package versions in `Directory.Packages.props`; warnings are errors. |
| Hosts | `src/AlertHub.Api`, `src/AlertHub.Ingest`, `src/AlertHub.Workers` | Minimal APIs; each exposes `/healthz/live|ready|startup`. Workers take `--roles=processing,scheduler,dispatcher`. |
| Migrator | `src/AlertHub.Migrator` | Applies EF migrations, then ensures `raw_event` partitions. Runs as the Helm pre-upgrade Job (ADR-10). Exit code 1 on failure. |
| First migration | `src/AlertHub.Infrastructure/Persistence/Migrations/*_InitialSchema.cs` | Schemas `alert`, `ops`, `audit`, `hb`, `cfg`; **partitioned** `alert.raw_event` (hand-written SQL, daily partitions); `ops.job` with the ★ one-live-timer-per-episode index; `ops.outbox`; `ops.hub_component_heartbeat`; `fillfactor=70` + aggressive autovacuum on the queue tables. |
| Partition management | `RawEventPartitions` | Idempotent; yesterday → today+7. Run by the migrator and daily by `PartitionCreateWorker` (scheduler role). |
| Health | `Infrastructure/Health` | `database` (reachable + no pending migrations) on every host; `outbox-lag` on the API host. Unhealthy ⇒ 503, degraded ⇒ 200. |
| Observability | `Infrastructure/Observability` | Serilog console JSON; OpenTelemetry traces + metrics with OTLP when `Otel:Endpoint` is set; `AlertHubMetrics` owns the ADR-9 instrument names. |
| Time | `TimeProvider.System` registered in every host | Domain/Application never call `DateTime.UtcNow`; tests swap in `FakeTimeProvider`. |
| Local stack | `docker-compose.yml` | `postgres` + `mailpit`; `--profile app` builds and runs migrator/api/ingest/workers. |
| Images | `build/Dockerfile` | One multi-stage Dockerfile, `--build-arg HOST=Api|Ingest|Workers|Migrator`, non-root runtime. |
| Helm | `deploy/helm/alert-hub` | Deployments + Services + PDBs for the three hosts, migrate Job hook, optional Ingress, all values from 03 §4. |
| CI | `.github/workflows/ci.yml` | Restore, build, `dotnet format` check, unit tests, integration tests against a PostgreSQL service, image builds for all four hosts, Helm lint/template. |

## Tests

- `AlertHub.Domain.Tests` — ids are UUIDv7 and time-ordered; severity parsing ranks `unknown` as `high`.
- `AlertHub.Contract.Tests` — OpenAPI document is served (no database needed).
- `AlertHub.Integration.Tests/Milestone0` — migrations apply cleanly; five schemas exist; `raw_event` is range-partitioned with partitions seven days ahead; partition creation is idempotent; inserts land in the daily partition; the one-timer-per-episode index rejects a second live timer; queue tables carry `fillfactor`/autovacuum options; `/healthz/*` answer 200 on the API host and `ready` answers 503 with an unreachable database.

Integration tests use Testcontainers, or an existing server when `ALERTHUB_TEST_CONNECTION` is set (each test class gets a throw-away database).

## Decisions made

- **Partition creation is DDL outside migrations.** The migrator ensures partitions after migrating and the scheduler creates them daily; ingestion never creates DDL. Without a default partition an insert for a day with no partition fails and ingest answers 503 (sources retry), which is the conservative behaviour AGENTS.md rule 11 asks for.
- **FluentAssertions pinned to 7.2.2** (last Apache-2.0 release); 8.x changed to a commercial licence.
- **Npgsql metrics** are collected via the `Npgsql` meter (`AddMeter("Npgsql")`); Npgsql 10 no longer ships a separate metrics instrumentation extension.
- **Host marker classes** (`ApiHost`, `IngestHost`, `WorkersHost`) instead of `public partial class Program` so one test project can host all three.
- **Migrations keep the scaffolded block-scoped namespace style** (folder-level `.editorconfig`), everything else is file-scoped.

## Not in this milestone

Seeded admin user, mock OIDC container and OpenAPI snapshot comparison arrive with Milestone 4 (auth). `kind` deployment rehearsal is part of the release checklist, not CI.
