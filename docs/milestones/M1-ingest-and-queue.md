# Milestone 1 — Durable ingest and job queue

Status: **done**. No owner input required (09).

## What exists

| Area | Where | Notes |
|---|---|---|
| Integration configuration | `Domain/Integrations/Integration`, `cfg.integration` | Immutable versions with `(integration_id, version)` PK, ★ one current version per id, type check constraint. Ingest credentials live on the version: rotation is a new version, so the old token dies at commit and the change is audited. `owner_team_id` is nullable until teams arrive (M3). |
| Credentials | `Application/Integrations/TokenGenerator`, `Infrastructure/Security/Argon2SecretHasher` | 12-char non-secret key id in the URL + 32-byte base64url secret in `Authorization: Bearer`. Argon2id, PHC encoding with per-row salt. Secrets are returned exactly once from `IntegrationService.Create/RotateIngestToken`. |
| Ingest endpoint | `AlertHub.Ingest/IngestEndpoints` | `POST /ingest/{keyId}` → 202 `{eventId, receivedAt}` / 401 / 413 / 429 (+`Retry-After`) / 503 (+`Retry-After`). Body stored as bytes (no JSON required); allow-listed headers only; source IP from forwarded headers. Rate limit: sliding window per key id, default 600/min. |
| Durable acceptance | `Application/Ingest/IngestService`, `Infrastructure/Ingest/EfIngestStore` | `raw_event` + `normalise` job in one `SaveChanges` (one transaction). 202 only after commit; any failure before commit is 503 so sources retry. |
| Job queue | `Application/Ops/IJobQueue`, `Infrastructure/Ops/JobQueue` | Claim = `FOR UPDATE SKIP LOCKED` CTE that reserves with a worker token and lease; complete/fail/extend are conditional on `(job_id, reserved_by, reserved_until > now)` ★. Failure = backoff (5 s · 2^n, full jitter, cap 1 h) until `max_attempts`, then `failed` (the failure queue). `RetryFailed` re-queues. |
| Runner | `Infrastructure/Ops/JobRunner` | One per role (`processing`, `scheduler`), handles the kinds it has `IJobHandler`s for, one DI scope per job, idle poll 1 s. No `normalise` handler exists yet (M2), so accepted events stay `pending` — durable, not lost. |
| Reaper | `Infrastructure/Ops/QueueReaperWorker` | Every 30 s releases expired job and outbox reservations. |
| Metrics | `QueueGauges` | `alerthub_job_queue_depth{kind}`, `alerthub_job_oldest_age_seconds{kind}`, `alerthub_ingest_accepted_total{integration}`, `alerthub_ingest_rejected_total{reason}`, `alerthub_scheduler_lag_seconds`. |
| Audit | `audit.entry`, `IAuditWriter` | `integration.create`, `integration.rotate_ingest_token`, `integration.enable/disable` written in the same transaction as the change. |
| Dev seed | `AlertHub.Migrator seed-dev` | Creates `dev-generic-webhook` and prints the ingest path + token once to stdout. |

## Acceptance scenarios (spec §22)

| Scenario | Test |
|---|---|
| Worker fails during processing | `WorkerFailureScenarios.Scenario_WorkerFailsDuringProcessing_*` — throwing handler is rescheduled with backoff and re-run; a worker that dies after claiming is reaped after its lease and the job re-runs on another worker; the raw event is untouched in both. |
| Worker fails after state commit (job part) | `WorkerFailureScenarios.Scenario_WorkerFailsAfterStateCommit_*` — the handler's committed state survives, the stale worker's `Complete` is rejected by the reservation check, the job re-runs. Outbox part lands in M3. |

Other tests: ingest 202/401/413/429/503 contract, header allow-list, rotation, disabled integration, random bytes never 500, queue ordering, concurrent claims never overlap, reservation token semantics, failure queue + retry, Argon2 encoding, worker role parsing, backoff bounds, token format.

## Decisions made

- **Token hashing parameters.** Argon2id `m=16 MiB, t=2, p=1` for 256-bit random tokens; ADR-14 parameters (`m=64 MiB, t=3, p=1`) for passwords. Successful ingest verifications are cached in-process for 5 minutes keyed by SHA-256(keyId ‖ version ‖ token) so a producer at 600/min does not pay Argon2 per request; failures are never cached; rotation changes the version and therefore the cache key.
- **Rate-limit partition by key id before authentication.** An unauthenticated caller can only exhaust the budget of the key it names; 429 never reveals whether the key exists.
- **503 also when the integration lookup itself fails** (database unreachable), with `Retry-After: 5`; the endpoint never returns 500 for infrastructure faults.
- **Credentials on the versioned row** (05 §6 lists them there). Rotation and enable/disable are new versions; that keeps a full history of who could send when.
- **Job payload JSON is camelCase** via `JsonDefaults.Stored`, shared by every stored JSON column.
- **Dapper not used.** EF Core `FromSqlInterpolated`/`ExecuteSqlInterpolated` carry the hand-written SQL with correct `timestamptz` ↔ `DateTimeOffset` handling; ADR-4 permits Dapper but does not require it.
