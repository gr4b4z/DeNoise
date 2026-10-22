# Pilot runbook

Spec §21 rollout, 09 M12. One team, legacy routes live in parallel (spec §7.3 point 2), eight weeks of measurement
against G1–G6. This is the checklist the pilot owner runs; every step names where in the product the evidence lives.

## Before the gate

| Step | Where | Done when |
|---|---|---|
| Shadow on the generic webhook producers for two weeks | Integration → Settings → *Shadow mode*; hub ingests and processes, stages no notifications | Divergence report per integration under the agreed threshold (`Pilot:DivergenceThreshold`, default 5 %) on three consecutive runs |
| Shadow on Azure Monitor and Atlas | same, per integration; Atlas has a state-query API so the report is quantitative, Azure is compared by hand (see the shadow checklist) | as above; Azure spot-check of 20 open episodes finds no more than one that Azure already resolved |
| Every §22 scenario green | `dotnet test` (Milestone folders map to scenarios; `docs/milestones/*` list them) and `pnpm e2e` | CI green on the release commit |
| Independent monitoring live | `docs/runbooks/hub-failure.md`: dead-man pinged, readiness probes, DB and lag alerts on the platform's path | dead-man drill alerts within 4 minutes of scaling workers to zero |
| Out-of-hours escalation target configured (**C7**) | Destinations: the external rotation system (JSM/Opsgenie-successor or phone tree) as a destination; escalation policy step for `critical`/`high` targets it | *Out-of-hours critical alert*: a test critical fired after hours reaches a person through that destination; the timeline shows the escalation and its target |
| Retention and backups | `/hub` → Retention shows a last run; `build/backup-restore-drill.sh` output filed | both within the last 7 days |
| Load test at the measured baseline (**C9**) | `build/loadtest/ingest-k6.js` with `BASELINE_RPS` | thresholds pass at ×1 sustained and ×3 burst |
| Owner questions closed | spec §23 items 4, 5, 8 (steady-state owner, out-of-hours target, data classification) | answers recorded in `docs/adr/` or the spec |

## Pilot start

1. Turn *Shadow mode* off for the pilot team's integrations only (Integration → Settings). Legacy routes stay live.
2. Confirm the team's destinations, fallback and escalation policy in Team overview → Destinations.
3. Announce to the team: what changes (they see episodes in the hub and get hub notifications in parallel), what does not (legacy paging), and where to report noise (a saved filter *pilot noise* on the queue).

## Weekly during the pilot

| Measure | Where | Goal |
|---|---|---|
| G1 duplicate notifications | `/history` filtered by team: episodes vs raw events accepted (Integration health: accepted last 15 m) | fewer notifications than legacy for the same conditions |
| G2 time to acknowledge | Team overview cards *ack overdue*; audit `episode.ack` timestamps vs `episode.opened` | trending down |
| G3 unowned alerts | Queue view *unassigned* for the team | zero at the weekly check |
| G4 stale alerts | Queue view *stale* | zero older than 7 days |
| G5 auto-resolve correctness | `/history` with `closureReason = inactivity_timeout` and `evidence = inactivity_unverified`; restores (`episode.restore` in audit) | restores < 2 % of auto-closes |
| G6 coverage gaps | Integration health per integration: coverage state, coverage episodes | no `unknown` coverage on a pilot integration |
| Divergence | Integration → Health → *Divergence*, run weekly | within threshold |

## Stop conditions (spec §21)

Stop the pilot and return the team to legacy-only when any of these holds: a critical condition was notified by the
legacy route but not by the hub (check the hub outbox/delivery attempts first); divergence above threshold on two
consecutive weekly runs; the hub was the only reporter of its own failure; the team cannot complete the core
workflows in the usability session.

## Pilot end

Run the usability session script (`docs/pilot/usability-session-script.md`), publish the G1–G6 table, then work the
decommission checklist per legacy route (`docs/pilot/decommission-checklist.md`) — only for routes whose conditions the
hub demonstrably covered during the eight weeks.
