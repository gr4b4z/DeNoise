# Runbook — Hub or monitored cluster fails

Spec §13.7 and §22 (*Hub or monitored cluster fails*): DeNoise must never be the only mechanism that can report its own
failure. This runbook describes what the independent monitor must watch, what each signal means and what to do.

## Independent signals (outside the hub, over an independent notification path)

| Signal | Source | Alert when | Meaning |
|---|---|---|---|
| Dead-man ping missing | External dead-man service pinged every minute by the scheduler (`DeNoise:ExternalDeadmanUrl`, `hub.deadman_pings_total`) | no ping for 3 minutes | scheduler role down, database down or outbound network gone: **no timers fire, no heartbeat misses are detected** |
| `GET /healthz/ready` on the ingest host | external HTTP probe | non-200 for 1 minute | sources receive 503 and retry; events are delayed, not lost, as long as the sources retry |
| `GET /healthz/ready` on the API host | external HTTP probe | non-200 for 1 minute | operators cannot see or act; processing continues |
| PostgreSQL availability | database platform monitoring | connection failures | everything stops; the outbox and queue preserve state for when it returns |
| Queue lag | `denoise_jobs_pending` / oldest pending age (OTel) scraped by the platform | oldest pending > 5 minutes | processing or scheduler role behind or down |
| Outbox lag | `denoise_outbox_pending` / oldest age | oldest pending > `Health:OutboxLagThreshold` | dispatcher role down or all destinations failing |

The hub health screen (`/hub`) shows the same facts from the inside; use it once the hub itself is reachable again.

## Response

1. **Confirm which role is down** from the platform: `denoise-api`, `denoise-ingest`, `denoise-workers` pods and their `/healthz/live`.
2. **Database down** → follow the platform's database runbook. Nothing to do in the hub; on return, the reaper releases
   stale reservations within 30 s and processing resumes from the queue. Check `/hub` for a failed-queue spike afterwards.
3. **Workers down, ingest and API up** → events are accepted and queued (`denoise_jobs_pending` grows). Restart the
   workers. Expect a burst of late `auto_resolve` / `escalation_step` timers; the guards (04 §5.3) recompute deadlines
   rather than closing a backlog.
4. **Ingest down** → sources retry (Azure Monitor and Atlas both do). Restart ingest; if it was down longer than the
   sources retry (Azure: ~1 h), use **Replay → historical** per integration only where the source can re-send, and tell the
   owning teams which window is uncovered.
5. **Whole cluster down** → the dead-man is the only signal. Page the platform team over the independent path. On
   recovery, run the Migrator job (idempotent), then verify `/hub`: components green, dead-man pinging, queues draining.
6. **After any outage**, check `/hub` → *Failure queue* and retry or dismiss entries; check integration health for
   coverage episodes opened during the outage (they close on the next successful signal).

## Verification

- Dead-man test: scale the workers to zero for 4 minutes in a non-production environment; the external monitor must alert.
- Backup/restore drill: `build/backup-restore-drill.sh <db>` (quarterly, keep the output).
- Load: `build/loadtest/ingest-k6.js` at the measured baseline (C9) before each capacity change.
