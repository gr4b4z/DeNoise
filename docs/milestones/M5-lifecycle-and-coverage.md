# Milestone 5 — Auto-resolve, administrative expiry, coverage state, dead-man ping

Status: **done**. Owner input C9 (baseline data for defaults) is still open; the presets follow the spec §12.2 table until then.

## What exists

| Area | Where | Notes |
|---|---|---|
| Lifecycle policies | `Application/Lifecycle/LifecyclePolicy.cs`, `LifecyclePolicyValidator` | Kind `lifecycle` in `cfg.policy` (ADR-6 versioning, impact preview, activate, rollback). Fields per 04 §7.3 / spec §12.3: `lifecycle.{profile, expected_repeat_interval, delivery_grace, min_inactivity}`, `auto_resolve.{enabled, after_silence, verify_before_close, on_coverage_unknown, on_coverage_degraded, escalate_before_close_if_severity, stale_escalation_lead}`, `expiry.{informational_after, unknown_lifecycle_review_after, unverified_expire_after, never_expire_severities}`. Every field falls back to the **profile preset**; `after_silence` absent ⇒ `max(3 × interval + grace, min_inactivity)`. Presets: `explicit_recovery` 60 min + API re-query where available, `queryable_state` 30 min, `repeating_while_active` formula, `one_shot` no inference / 24 h informational retention, `unknown` 60 min with evidence forced to `inactivity_unverified`. |
| Resolution order | `LifecycleScheduler.ResolveAsync` | routing rule `lifecycle_policy` (new field, 04 §7.3 "per rule match") → the policy the episode already carries → integration `profile_defaults` (`lifecycle_policy` reference or inline document) → mapping `lifecycle_profile_hint` → preset for the integration type (Azure `explicit_recovery`, Atlas `queryable_state`, generic `unknown`). |
| Timers | `LifecycleScheduler.ScheduleAsync` (called from the transition hook on open and on every effective signal) | `auto_resolve` at `last_seen + timeout`, `stale_review` (unknown profile) and `admin_expiry` (severity ∉ never-expire) from `last_seen`, `informational_expiry` for non-actionable episodes. Timers carry `expected_version` **and** `expected_last_seen`; every signal cancels and re-stages them. Coverage episodes (`profile = coverage`) get no timers. |
| Auto-resolve guards | `LifecycleJobHandler.AutoResolveAsync` | 04 §5.3 in order: (1) stale timer (both version and last_seen moved) ⇒ recorded, nothing else; (2) due recomputed from `last_seen`, else rescheduled; (3) coverage `degraded`/`unavailable` ⇒ job **suspended**, condition `unknown` (or `close_unverified` per policy); coverage never established + `on_coverage_unknown: suspend` ⇒ suspended; (4) `normalise` backlog older than `Lifecycle:ProcessingDelayThreshold` (5 min) ⇒ postponed; (5) ≥ `Lifecycle:MappingFailureThreshold` (5) mapping failures in 15 min ⇒ postponed; (6) policy re-resolved — disabled ⇒ stop; (7) `verify_before_close` through `IStateQueryAdapter` when the integration has the `state_query` capability (`active` ⇒ reschedule, `not_active` ⇒ `verified_resolved`/`api_verification`, `error` + `api_required` ⇒ suspend); (8) critical/high ⇒ one `episode.stale_critical` to the owning team, then `stale_escalation_lead` (30 min) before closing. Evidence: coverage healthy since before `last_seen` ⇒ `heartbeat_and_inactivity`, otherwise `inactivity_unverified`. |
| One live timer per episode | `JobContext.Retained`, `IProcessingSession.RescheduleJobAsync/SuspendJobAsync` | Handlers move the claimed row (reschedule/suspend) instead of inserting a second one; the runner skips completion for a retained job. |
| Expiry and review | `AdminExpiryAsync`, `StaleReviewAsync`, `InformationalExpiryAsync` | `stale_review` sets `episode.stale_since` (new column) — the Stale/unverified view and the `stale` flag include it; `admin_expiry` closes as `expired_unverified` with evidence `none` and the **last known condition preserved**; informational closes as `informational_completed`. |
| Coverage config | `Application/Coverage/CoverageConfig.cs` | Parses `cfg.integration.coverage` (spec §13.5 YAML shape): `managed_canary` with `expected_interval`, `delayed_after`, `alert_after`, `unavailable_after`, `recovery_successes_required`; `registered_heartbeat` ids are recorded for milestone 6; `api_probe` is accepted but never raises coverage on its own. |
| Coverage state machine | `CoverageMachine` (pure), `CoverageEvaluator` | 04 §9: signal-driven (`unknown/delayed → healthy`, `degraded/unavailable → healthy` after N consecutive successes, explicit failure ⇒ `degraded`) and time-driven (`healthy → delayed → degraded → unavailable`). Never-established coverage does not degrade on silence (spec §12.4). Consequences in one transaction: `degraded` ⇒ suspend `auto_resolve`/`verify_state` jobs of the integration, open episodes ⇒ condition `unknown`, **one coverage episode per outage** (identity `coverage:{integration_id}`, severity high, owner = integration owner or triage, `coverage.lost` to its destinations, summary as spec §13.6); `unavailable` ⇒ severity critical on the same episode; `healthy` ⇒ resume jobs (due no earlier than now), condition back to `firing`, coverage episode closed `source_resolved`, `coverage.restored`. `coverage.changed` SSE event after commit. |
| Signals | `EventProcessor` heartbeat branch → `ICoverageSignalSink` | Canary/heartbeat events never touch episodes; a label `canary: fail` is an explicit failure. Without a configured method the signal is remembered (`last_signal_at`) but coverage stays `unknown`. |
| Scheduler tick | `Workers/Scheduling/CoverageCheckWorker` | Every 30 s, all current integrations with a canary method (`coverage_check`). |
| Dead-man ping | `Workers/Scheduling/DeadmanPingWorker`, `DeNoise:ExternalDeadmanUrl` (Helm `externalDeadmanUrl`) | Every minute, only after the database answers; last success recorded as component `deadman` for `/hub/health`. Missing URL logs a launch-blocker warning (spec §13.7). |
| Impact preview | `PolicyImpactService`, `POST /api/v1/policies/{kind}/{id}/{v}/impact` | Lifecycle: count + sample of open episodes on the policy with current and proposed deadlines. Routing: open episodes whose matched rule changes team/escalation/lifecycle or disappears (open episodes are never re-routed). |
| Activation | `LifecycleActivationHook` (`IPolicyActivationHook`, post-commit) | Reschedules the timers of every open episode on the policy from `last_seen`; the earliest new deadline is one minute out — a backlog is never closed at activation. |
| Health | `GET /api/v1/integrations/{id}/health`, `GET /api/v1/hub/health`, `GET/POST /api/v1/hub/failures[/{id}/retry]` | Coverage state/since/last signal/successes, coverage episode, last processed alert, accepted and mapping failures (15 min), backlog, suspended closures, open episodes; hub components (fresh ≤ 90 s), queue depth per kind, oldest due job, outbox lag, failed counts, dead-man status (ok ≤ 3 min); failure queue with retry (jobs via the queue, outbox rows re-queued). |
| Read model | `EpisodeQueries`, `EpisodeExplanation` | `stale` = condition unknown **or** reviewed; the explanation carries the spec §12.3 text (*Automatically resolved after N minutes without another signal. Source delivery remained verified healthy throughout / was not verified. Recovery was inferred from the configured repetition policy, not reported by the source.*) and the review sentence. |
| Schema | migration `LifecycleAndCoverage` | `alert.episode.lifecycle_policy_id`, `alert.episode.stale_since`. Destinations now subscribe by default to `episode.follow_up_due` and `episode.stale_critical` too. |

## Acceptance scenarios (spec §22)

| Scenario | Test (`Milestone5/LifecycleScenarios`) |
|---|---|
| Repeating source becomes quiet, coverage healthy | `Scenario_RepeatingSourceBecomesQuiet_*` — canary healthy throughout; closed `inactivity_timeout` / `heartbeat_and_inactivity`, condition `resolved`, `episode.closed` to prior recipients, remaining timers cancelled. |
| Source becomes quiet, coverage never configured | `Scenario_SourceQuiet_CoverageNeverConfigured_*` — closes on the timer, evidence `inactivity_unverified`, recorded on the timeline. |
| Azure `resolved` lost in transit | `Scenario_AzureResolvedLostInTransit_*` — `explicit_recovery` 60 min backstop; `episode.stale_critical` first (high severity), closure after the lead, evidence never `source`. |
| Operator changes an auto-resolve timeout | `Scenario_OperatorChangesAutoResolveTimeout_*` — preview counts 1 with the proposed deadline; activation reschedules; shortening below the elapsed silence leaves a one-minute deadline and closes nothing instantly. |
| Canary rule stops firing | `Scenario_CanaryRuleStopsFiring_*` — past `alert_after`: coverage episode (owner team, `coverage.lost`), `auto_resolve` suspended, condition `unknown`, no closure while suspended, no timers on the coverage episode; three successes restore, resume and close the coverage episode as `source_resolved` with `coverage.restored`; the resumed timer closes with `inactivity_unverified`. |
| Coverage alert flaps | `Scenario_CoverageAlertFlaps_*` — success/fail alternation keeps one episode and `degraded` until three consecutive successes. |
| Ingestion or mapping unhealthy | `Scenario_IngestionOrMappingUnhealthy_*` — five mapping failures postpone closure by 5 min with a timeline note. |
| Old alert reaches administrative expiry | `Scenario_OldAlertReachesAdministrativeExpiry_*` — unknown profile, medium: review at 24 h (`stale_since`), expiry at 7 d as `expired_unverified`, evidence `none`, condition still `firing`. |
| New signal races with expiry | `Scenario_NewSignalRacesWithExpiry_*` — the update re-stages the timer; a timer with stale guards runs and refuses. |
| Source resumes after expiry | `Scenario_SourceResumesAfterExpiry_*` — new episode with `previous_episode_id`, the expired one untouched. |

Units: `LifecyclePolicyTests` (formula, presets, overrides, validation paths), `CoverageMachineTests` (all transitions, flap protection, never-established rule, config parsing).

## Decisions made

- **`unknown` profile still infers recovery (60 min, `inactivity_unverified`)** as the spec §12.2 preset table says; §12.1's "never infer" reading is available by setting `auto_resolve.enabled: false`, which is exactly what the administrative-expiry scenario does.
- **Guard 1 refuses only when both `version` and `last_seen` moved.** A note or an acknowledgement bumps the version without changing the silence window; the timer stays valid and recomputes (04 §5.3 "allow, recompute").
- **Evidence `heartbeat_and_inactivity` requires coverage `healthy` since before `last_seen`.** A recovery that happened during the silence window means the silence was not verified throughout, so the closure is labelled `inactivity_unverified`.
- **Coverage episodes are opened directly by the evaluator**, not through ingest: no raw event exists, the identity is synthetic (`coverage:{integration_id}`) and they carry the internal profile `coverage`, which the scheduler treats as "no lifecycle timers".
- **Signals without a configured method never establish coverage.** Otherwise a single stray heartbeat would turn "never configured" into "was healthy, now failing" and suspend closures for a source that has no canary (spec §12.4's 0.3 flaw).
- **Postpone means +5 min and a timeline entry**, not suspension: backlog and mapping failures are transient, coverage loss is not.
- **Expiry keeps the review/expiry timers on every signal** (re-staged from `last_seen`), so a source that keeps firing is never expired; only true silence ages out.
- **Routing impact never re-routes open episodes**; it reports which rules changed and how many open episodes they routed. Re-routing on activation would silently change ownership.
- **Failure retry for outbox rows resets attempts**; jobs go through the queue's own retry path.

## Configuration

```
Lifecycle:ProcessingDelayThreshold   00:05:00   # guard 4
Lifecycle:MappingFailureThreshold    5          # guard 5, within Lifecycle:MappingFailureWindow (00:15:00)
Lifecycle:PostponeBy                 00:05:00
DeNoise:ExternalDeadmanUrl          https://…  # spec §13.7; every scheduler replica pings once a minute
```
