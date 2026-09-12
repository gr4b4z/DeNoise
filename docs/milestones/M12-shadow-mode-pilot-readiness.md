# Milestone 12 — Shadow mode, divergence report, pilot readiness

Status: **done** for what can be built without owner input. Open owner inputs: **C7** (out-of-hours escalation target — spec §23 item 5) gates the *Out-of-hours critical alert* scenario; the pilot team.

## What exists

| Area | Where | Notes |
|---|---|---|
| Shadow flag | `Integration.Shadow` (since M8), `PUT /integrations/{id}` `shadow`, Settings tab checkbox, badge in list and detail | Ingest and processing run in full (episodes, timers, coverage, audit); `NotificationTransitionHook`, escalation, lifecycle and action paths stage **no outbox rows**. Verified again in `Milestone12/DivergenceScenarios` (four episodes, zero outbox rows). |
| Divergence report | `Application/Divergence/Divergence.cs`, `Infrastructure/Integrations/DivergenceReporter.cs` | For one integration, every open actionable episode is asked of the source through the `state_query` adapter (Atlas Admin API today; Azure has none). Counts: **agree** (source active), **diverged** (source not active, hub open), **unknown** (source did not answer). `divergenceShare = diverged / (agree + diverged)`; `withinThreshold` against `Pilot:DivergenceThreshold` (default 5 %). Up to `Pilot:SampleSize` divergent/unknown episodes are listed by id. Sources without a state API return `supported = false` with an explanation instead of a verdict; with no open episodes the adapter's probe decides `supported` (so a fresh Azure integration reads *not supported*, a fresh Atlas one *no open episodes to compare yet*). Every run is stored as an `integration.divergence_report` audit entry; the last one is what the API and UI show. |
| API | `GET /api/v1/integrations/{id}/divergence` (`integration.read`; 204 when never run), `POST /api/v1/integrations/{id}/divergence` (`integration.manage`, CSRF) | Scope-checked like the other integration endpoints. |
| UI (`web/`) | `IntegrationDetail` → Health tab → *Divergence (shadow gate)* | Last report with time, open count, agree / diverged / unknown badges, the verdict sentence (share vs threshold, or *not supported* with the reason), the sample list linking to episodes; *Run check* for `integration.manage`. |
| Pilot runbook | `docs/pilot/pilot-runbook.md` | Spec §21: before-the-gate checklist (shadow weeks, §22 scenarios, independent monitoring, C7 target, retention/backup, load test, open questions), pilot start, weekly G1–G6 measures with where to read them, stop conditions, pilot end. |
| Usability session script | `docs/pilot/usability-session-script.md` | Nine tasks over the core workflows (spec §22 "successful operator completion"), pass criteria per task, debrief questions, recording table. |
| Decommission checklist | `docs/pilot/decommission-checklist.md` | Ten conditions per legacy route, with evidence locations; keeps `critical`/`high` routes until the rotation target exists (spec §7.3 point 2). |

## Acceptance

| Item | Test / record |
|---|---|
| Shadow processes but never notifies; report counts and verdict; unknown excluded from the share; sample lists divergent and unknown only; stored and read back; a second run after the source recovers is within threshold | `DivergenceScenarios.Shadow_integration_processes_but_never_notifies_*` |
| Sources without a state API: `supported = false`, no share, no verdict | `Sources_without_a_state_api_report_unsupported_instead_of_a_share`, `With_no_open_episodes_the_probe_decides_whether_the_source_is_supported` |
| UI | Playwright `e2e/integrations.spec.ts` — the Health tab runs a divergence check on the wizard's Azure integration and shows *not supported* with the reason (Azure Monitor has no state-query API). |
| Out-of-hours critical alert | **Blocked on C7.** The mechanism exists (escalation policy steps target any destination; the timeline records the escalation and its target); the scenario record is a step in the pilot runbook once the external rotation system is named. |

## Decisions made

- **Divergence is measured per open episode, not per source alert list.** The adapters answer "is this condition still active?"; listing every active alert at the source would need a second adapter surface per integration type. The per-episode question is the one that matters for the gate (hub says open, source says resolved = missed recovery).
- **Unknown answers count neither way.** A timeout is not evidence of divergence; the report shows them so a noisy source API is visible.
- **The report lives in the audit log**, like the retention run: no new table, the history of runs is the audit trail, and the shadow weeks can be reviewed later.
- **11b (OIDC) not built.** It is gated on **C2** (Entra ID tenant details) in 09; local auth remains the only provider.
