# Milestone 11 — Retention, hub health, hardening

Status: **done** (backend, hub health screen, failure queue UI, payload-retention flag, hardening scripts and runbook). Owner inputs still open: **C9** (measured peak, to parameterise the load test) and **C10** (Helm/GitOps conventions).

## What exists

| Area | Where | Notes |
|---|---|---|
| Retention options | `Application/Retention/Retention.cs` (`RetentionOptions`, section `Retention`, Helm `retention.*`) | Spec §18.2 values: raw 30 d, normalised (closed) 90 d, closed episodes 12 months, delivery attempts 30 d, audit 12 months, sessions 7 d after expiry, login attempts 30 d, 5,000 rows per statement, daily at 02:00 UTC. |
| `retention_raw` | `Infrastructure/Retention/RetentionService.RunAsync`, `RawEventPartitions.DropAsync` | Drops whole `alert.raw_event` partitions whose day is older than *today − rawDays*; **today−1 and newer are never candidates** (05 §7). Each drop is its own audit entry (`retention.raw_partition_drop`, partition name as target). |
| `retention_rows` | `RetentionService.RunAsync` | Batched key-select + `ExecuteDelete` per table, oldest first: normalised events older than 90 d **only when their episode is closed** (or they never attached to one); closed episodes older than 12 months (timeline cascades); delivery attempts, terminal outbox rows (`sent`/`coalesced`/`suppressed`/`cancelled`) and done/cancelled jobs older than 30 d; audit older than 12 months; sessions expired or revoked more than 7 d ago; login attempts older than 30 d; expired idempotency keys; `hb.run` beyond the per-heartbeat ring (`Heartbeats:RunRingSize`). **Open episodes and their events are never touched.** The run is recorded as one `retention.run` audit entry carrying the report (dropped partitions, rows per table, duration). |
| Scheduling | `Workers/Scheduling/RetentionWorker` (scheduler role) | Sleeps until the next `RunAtUtcHour`, runs, repeats; a failed run logs and waits for the next day. `RetentionSchedule.NextRun` is pure and tested. |
| API | `GET /api/v1/hub/retention` (`hub.health.read`), `POST /api/v1/hub/retention/run` (`hub.admin`, CSRF) | Status: settings, raw boundary instant, partition range and count, last run report (from the audit row), next run. Run: executes now and returns the report. |
| Payload-retention flag | `EpisodeDetail.RawPayloadAvailable` (existing) + UI | The detail read model already checks whether the oldest raw partition of the episode still exists; the UI half shows *original payload no longer retained* on the overview and the raw endpoint answers 404 while the episode stays actionable (spec §22). |
| Hub health API (since M5) | `GET /hub/health`, `GET /hub/failures`, `POST /hub/failures/{id}/retry` | Now covered over HTTP (`Milestone11/HubApiTests`): permission matrix (operators read health and retention status; the failure queue, retry and the manual retention run need `hub.admin`). |
| Rate-limit tuning | Helm `limits.apiPerMinutePerUser`, `limits.loginPerMinutePerIp` → `Limits__*` env; `retention.*` knobs (sessionExpiredDays, loginAttemptDays, batchSize, runAtUtcHour) | The API already read `Limits:ApiPerMinutePerUser` / `Limits:LoginPerMinutePerIp`; the chart now exposes them (06 §6). |
| Load test | `build/loadtest/ingest-k6.js` | k6: `sustained` at baseline ×1 for 10 min, then `burst` at ×3 for 2 min; thresholds p95 accept < 250 ms and no failures in `sustained`, 429s allowed only in the burst. `BASELINE_RPS` is owner input **C9**. |
| Backup/restore drill | `build/backup-restore-drill.sh` | `pg_dump` (custom format) → `pg_restore` into a scratch database → row counts of every table that matters compared → optional Migrator run against the copy (idempotency) → scratch dropped. Exit code is the verdict. |
| Runbook | `docs/runbooks/hub-failure.md` | Spec §22 *Hub or monitored cluster fails*: the independent signals (dead-man, readiness probes, database, queue/outbox lag), what each means, the response per failed role, and the verification drills. |
| UI (`web/`) | `src/hub/queries.ts`, `src/routes/Hub.tsx`, `Episode.tsx`, `Shell.tsx` | **Hub health** (`/hub`, rail link for `hub.health.read`): five cards (database, components reporting, outbox pending · oldest age, failed jobs + outbox, external dead-man with a spec §13.7 hint when unconfigured), component heartbeat table, per-kind queue table (pending / reserved / suspended / failed / oldest age), **retention** section (raw boundary and days, partition range, row rules in one sentence, next and last run with the report; *Run retention now* for `hub.admin` with confirmation and the resulting report), **failure queue** (`hub.admin`): when, source, type, attempts, last error, episode link, *Retry*. Auto-refresh every 15 s. **Episode overview** shows *Original payload no longer retained …* when `rawPayloadAvailable` is false. |

## Acceptance scenarios (spec §22)

| Scenario | Test |
|---|---|
| Raw payload retention expires on an open episode | `Milestone11/RetentionScenarios.Scenario_RawPayloadRetentionExpiresOnAnOpenEpisode_*` — an open episode's raw payload is moved into a 40-day-old partition; the run drops exactly that partition (today's and the days ahead stay), the episode stays open with its explanation, `RawPayloadAvailable` turns false and the raw-payload read returns nothing; drop and run are audited; status shows the last run and the next 02:00 UTC. |
| Rows by age, open episodes untouched | `Rows_are_deleted_by_age_in_batches_and_open_episodes_are_never_touched` — with batch size 2: the 13-month-old closed episode goes (timeline cascades), the recently closed and the open one stay; normalised events of closed episodes go, the open episode keeps its history; 5 old audit / delivery / login rows go, recent ones stay; a second run deletes nothing. |
| Schedule | `Next_run_is_the_next_two_oclock_utc`. |
| Hub API | `HubApiTests` — health for operators, failures 403 / 200 by role, retry of an unknown id 404, retention status (30 d, partitions, never ran, next run), manual run 403 / 200 with the report, status then shows the run, audit lists `retention.run` by *DeNoise retention*. |
| Hub or monitored cluster fails | Runbook `docs/runbooks/hub-failure.md` + the dead-man worker (M5) + independent probes; the drill is a manual verification step, not an automated test. |
| UI | Playwright `e2e/hub.spec.ts` — the hub screen shows five cards with the database green, component heartbeats, the retention section and the failure queue; *Run retention now* produces a report and the last-run text updates. axe passes. |

## Decisions made

- **Terminal outbox rows and done jobs follow the delivery-attempt retention (30 d).** 05 §7 lists neither table; without a rule they grow forever. Pending, reserved, suspended and failed rows are never deleted by retention (the failure queue is the operator's, spec §17.3).
- **Unattached normalised events age out too.** "Only where episode closed" protects open episodes; an event that never joined an episode (heartbeats, informational without a policy) has nothing to protect.
- **Batches by key list, not `LIMIT` deletes.** Portable EF (`ExecuteDelete` on `id IN (…)`), 5,000 keys per statement, oldest first; a long backlog is drained over several statements without long locks.
- **The last run lives in the audit log.** No new table: `retention.run` carries the report JSON, and the API/UI read the newest one. It is also the natural place for an operator to look for "what did retention remove".
- **Partition drops are per-partition audit entries** so a dropped day is findable by name later.
