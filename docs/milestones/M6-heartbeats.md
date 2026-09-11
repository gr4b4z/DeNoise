# Milestone 6 (backend) — Registered heartbeats

Status: **backend done**; the heartbeat list/create/detail screens (08 §3.4) follow with the next UI slice. No open owner inputs.

## What exists

| Area | Where | Notes |
|---|---|---|
| Model | `Domain/Heartbeats/Heartbeat.cs`, migration `Heartbeats` | `hb.heartbeat` and `hb.run` exactly as 05 §5 plus `run_started_at` (duration tracking for `/start`) and `paused_by_maintenance` (so a window's automatic pause can be told from a person's). Indexes `hb_key_idx` (unique key id), `hb_due_idx` (partial on `expected_next` for healthy/late), team and binding indexes. |
| Schedule | `Application/Heartbeats/HeartbeatSchedule.cs` | Interval ⇒ `last_ping + interval`; cron ⇒ Cronos in the declared IANA zone, returned in UTC (ADR-8). Spring-forward: the occurrence that does not exist runs once at the first valid instant, autumn's repeated hour fires once — no missing and no double alarm (spec §13.3.3). Validation, "next N runs" preview and a human description for lists. **New package: Cronos** (Application) — cron with `TimeZoneInfo`, the ADR-8 choice. |
| State machine | `HeartbeatMachine` (pure) | 04 §10: first ping ⇒ healthy; tick past `expected_next` ⇒ late (UI only); past `expected_next + grace` ⇒ missed; `/fail` or non-zero `/exit` ⇒ missed immediately; `recovery_successes_required` consecutive pings ⇒ healthy; pause(actor, reason) ⇒ paused, pings recorded but not evaluated; resume ⇒ unknown with `expected_next` from now. `/start` only records the run start. |
| Ping API | `Ingest/HeartbeatPingEndpoints.cs`, `HeartbeatPingService`, `HeartbeatPingAuthenticator` | `GET|POST /hb/{keyId}.{token}`, `POST …/start`, `…/fail`, `…/exit/{code}` on the **ingest host**; 200 `{"ok":true}` after commit, 404 for unknown key and wrong token alike (a dummy Argon2 verification equalises timing; successes cached per rotation), 429 per key id (`Heartbeats:PingsPerMinutePerKey`, default 60). Body ≤ 16 KiB kept as the run's output, tail-truncated on a UTF-8 boundary. One transaction: `UPDATE hb.heartbeat … FOR UPDATE`, one `hb.run` row, ring trimmed to `Heartbeats:RunRingSize` (100). No raw event, no job. |
| Miss episodes | `HeartbeatEffects` | One ordinary episode per outage (identity `heartbeat:{id}`, severity `severity_on_miss`, owner and assignee from the definition, internal profile `heartbeat` ⇒ no lifecycle timers). Opened through the transition hook's new `OnPreassignedOpenAsync`: ack deadline from the team's default escalation policy, timeline "ownership set by the heartbeat definition", `heartbeat.missed` to the team's destinations. Recovery closes it as `source_resolved` with evidence **`source`** (a ping is confirmed recovery) and stages `heartbeat.recovered` plus `episode.closed` to prior recipients. Repeated misses land on the open episode's timeline. Miss episodes without a bound integration belong to the inactive system integration `alert-hub-heartbeats` (episodes need an integration row; it can never ingest). |
| Scheduler | `HeartbeatMonitor`, `Workers/Scheduling/HeartbeatCheckWorker` | Every 30 s: due heartbeats claimed with `FOR UPDATE SKIP LOCKED` (replicas share the work) move healthy → late → missed; maintenance sweep pauses heartbeats whose scope a window covers and resumes them when it ends (`IMaintenanceWindows`; milestone 9 supplies the calendar, until then nothing is covered). Missing is detected here, never by a ping. |
| Coverage binding | `CoverageEvaluator.OnBoundHeartbeatAsync` | A bound heartbeat is the integration's coverage signal (spec §13.3.3): a miss ⇒ `degraded` (coverage episode, timers suspended, conditions unknown — milestone 5 machinery), every successful ping counts towards `recovery_successes_required`. |
| Management API | `Api/Endpoints/HeartbeatEndpoints.cs`, `HeartbeatService` | `GET/POST /api/v1/heartbeats`, `GET/PUT/DELETE /{id}` (`If-Match`), `POST /{id}/pause { reason }`, `/resume`, `/rotate-token` (new secret, same key id, identity/history/state kept; old token dead at commit), `POST /preview` (next runs for the form), `GET /export?team=` (YAML, tokens never exported), `POST /import { yaml, dryRun }` (by name within a team: create / update / unchanged with a field diff; never deletes; ping URLs returned once for created ones). Permissions: `heartbeat.manage` for mutations, `episode.read` for reads, scope-checked; CSRF on mutations. Ping URL = `Heartbeats:IngestPublicBaseUrl` (Helm `ingestPublicBaseUrl` via `AlertHub:IngestPublicBaseUrl`) + `/hb/{keyId}.{secret}`, shown at creation and rotation only. |
| Realtime | `heartbeat.changed` SSE event | `{ id, state, version, expectedNext, lastPingAt }` after every committed transition; touched episodes publish as usual. |

## Acceptance scenarios (spec §22)

| Scenario | Test (`Milestone6/`) |
|---|---|
| Registered heartbeat misses its schedule | `HeartbeatScenarios.Scenario_RegisteredHeartbeatMissesItsSchedule_*` — late first (no episode), missed past grace: one high episode owned by the team, `heartbeat.missed` to its destination, no lifecycle timers; the next ping closes it `source_resolved` / `source` with `heartbeat.recovered` and `episode.closed`. |
| Heartbeat misses many intervals in a row | `Scenario_HeartbeatMissesManyIntervals_*` — six hourly ticks, one episode. |
| Cron heartbeat crosses a DST transition | `HeartbeatScheduleTests` — Europe/Warsaw 02:30 on 2026-03-29 runs once at 03:00 CEST, on 2026-10-25 the repeated hour fires once; property test over three zones and six transition dates: `expected_next` strictly increasing, one occurrence per day. |
| Heartbeat job calls `/fail` | `Scenario_HeartbeatJobCallsFail_*` — missed without waiting for grace, run duration from `/start`, output tail stored; `/exit/0` recovers. |
| Heartbeat token is rotated | `Scenario_HeartbeatTokenIsRotated_*` — old URL 404-equivalent, new one works, same id, key id, state and run history. |
| Heartbeat is paused | `Scenario_HeartbeatIsPaused_*` — paused with actor and reason, open miss closed `manual_close`, two days of ticks change nothing, resume ⇒ unknown with a fresh deadline, no false miss. |
| Maintenance window covers a heartbeat's scope | `Scenario_MaintenanceWindowCoversHeartbeatScope_*` — scripted calendar: auto-pause by maintenance (no actor), resume at window end, no false miss. |
| Alert Hub scheduler stops | covered by the milestone-5 dead-man ping and `/hub/health` `deadman` status; the external switch, not the Hub, reports the stop (spec §13.7). |

Also: bound heartbeat drives integration coverage (degraded on miss, healthy after two pings), YAML export/import round trip with dry-run diff, ping contract on the ingest host (200 body, 404 for both failure kinds, per-key 429).

## Decisions made

- **The deadline counts from registration.** A heartbeat is `healthy` with `expected_next = now + interval` at creation, so a job that never runs at all is still caught; `unknown` is reserved for "resumed, waiting for the first ping".
- **`unknown` is not evaluated by the tick.** After a resume the heartbeat has no deadline until the first ping, which is what "resume without a false miss" requires; a job that never pings again after a pause is caught the next time a person looks at the paused/unknown list — not silently, since `unknown` is not `healthy`.
- **Pings while paused are recorded, never evaluated**, so the run history stays complete for the post-mortem.
- **A bound heartbeat's successful pings always reach coverage**, not only the recovery transition; otherwise a threshold above one could never be met.
- **Cron occurrences are stored in UTC** even though Cronos computes them in the declared zone (Npgsql accepts only offset 0 for `timestamptz`).
- **Import never deletes.** A heartbeat missing from the YAML is left alone; deleting is an explicit API call with `If-Match`.
- **Delete closes an open miss episode as `manual_close`** with the reason "heartbeat deleted" — the alert must not outlive its definition.
- **Ping URLs come from `Heartbeats:IngestPublicBaseUrl`**, falling back to the Helm-provided `AlertHub:IngestPublicBaseUrl`; the same fallback now applies to `Notifications:PublicBaseUrl` ← `AlertHub:PublicBaseUrl`.

## Configuration

```
Heartbeats:IngestPublicBaseUrl   http://localhost:8081   # or AlertHub:IngestPublicBaseUrl (Helm ingestPublicBaseUrl)
Heartbeats:PingsPerMinutePerKey  60
Heartbeats:BodyBytes             16384
Heartbeats:RunRingSize           100
```
