# Milestone 3 — Ownership, routing, outbox, dispatcher

Status: **done**. Owner input C4 (SMTP relay) is still open; the SMTP channel is configured for Mailpit until then.

## What exists

| Area | Where | Notes |
|---|---|---|
| Teams and scopes | `cfg.team`, `cfg.scope`, `TeamService` | ★ at most one triage team (`is_triage` partial unique index); scopes seeded from config later (C6). |
| Destinations | `cfg.destination`, `DestinationService` | Two channel types (`webhook`, `smtp_email`); URL/headers/signing secret encrypted at rest with ASP.NET Data Protection; ★ `fallback_destination_id` mandatory and ≠ self (check constraint + deferrable FK so a bootstrap pair is created in one transaction); event-type subscriptions; health columns updated by the dispatcher. Signing secret is returned exactly once. |
| Policies | `cfg.policy`, `PolicyService`, `IPolicyValidator` | One versioned table with a `kind` discriminator (routing, escalation, lifecycle, grouping); ★ one active version per (kind, id); create → validate → inactive; activate; rollback = activate an earlier version. Routing has a single well-known document id. |
| Routing | `Application/Routing/RoutingPolicy`, `RoutingEngine` | 04 §7.1: priority order, `stop: false` accumulates destinations, first team wins; refs are canonical fields + `labels.*`, `dimensions.*`, `integration.*` — never raw JSON; no match ⇒ triage team + `routing_correction_required` + `episode.routing_failure`; every decision carries a plain-language `why` stored on the timeline. |
| Escalation | `EscalationPolicy`, `Scheduling/EscalationJobHandlers` | 04 §7.2: `ack_deadline`, ordered steps with `team_destinations` / `destination:<id>` / `external:<name>` (resolved to a destination of that name — C7) / `user:<id>` (M4), `repeat_last_step_every`, `max_repeats`. The `ack_deadline` timer fires `episode.ack_overdue` only if the episode is still open and `new`, then schedules the next step in the same transaction. |
| Notification triggers | `NotificationTransitionHook` | Inside the processing transaction: `episode.opened` (actionable only), `episode.escalated_severity` on material increase, `episode.closed` to prior recipients with timer cancellation; suppressed episodes get rows in status `suppressed`; shadow integrations get none. |
| Notification model | `NotificationModel` | The 06 §7 envelope; `deliveryId`/`sentAt` filled at send time; links use `Notifications:PublicBaseUrl`. |
| Dispatcher | `OutboxDispatcher`, `DispatcherWorker`, `EfOutboxQueue` | Claim with reservation token → relevance/coalescing (closed episode, acknowledged for `ack_overdue`, later `episode.closed` row) → render `generic-json` → send → `ops.delivery_attempt` → sent / rescheduled (30 s → 1 m → 5 m → 15 m → 1 h, `Retry-After` honoured, max 10) / failed. Permanent failure or exhausted attempts ⇒ one fallback row + `hub.delivery_failure` to the fallback (single hop). |
| Webhook channel | `WebhookChannel` | ADR-7 contract: `POST`, `User-Agent`, `X-AlertHub-Delivery-Id` (stable across retries), `X-AlertHub-Event`, `X-AlertHub-Timestamp`, `X-AlertHub-Signature: v1=hex(hmac_sha256(secret, ts + "." + body))`, destination headers, per-destination timeout, no redirects, https required unless `AllowInsecureDestinations`; 2xx success, 408/425/429/5xx retryable, other 4xx/3xx permanent, timeout ⇒ `response_lost`; first 4 KiB of the response kept. |
| SMTP channel | `SmtpEmailChannel` | MailKit; subject `[AlertHub][severity] summary — resource`; plain text only; `Message-Id` from the outbox id, `References` to the episode id for threading; `Smtp:*` options (Mailpit: `Security=None`). |
| Schema | migration `RoutingAndNotifications` | `cfg.team`, `cfg.scope`, `cfg.destination`, `cfg.policy`, `ops.delivery_attempt`. |
| Dev seed | `Migrator seed-dev` | Also creates the `triage` team. |

## Acceptance scenarios (spec §22)

| Scenario | Test |
|---|---|
| No ownership rule matches | `RoutingAndOutboxScenarios.Scenario_NoOwnershipRuleMatches_*` — team = triage, `routing_correction_required`, unassigned, `episode.routing_failure` outbox row to triage destinations, audit row. |
| Worker fails after state commit (outbox part) | `Scenario_WorkerFailsAfterStateCommit_outbox_*` — rows are pending after the processing commit with nothing else running; the dispatcher sends them, records attempts and destination health. |
| Delivery succeeds but response is lost | `Scenario_DeliverySucceedsButResponseIsLost_*` — attempt `response_lost`, retry after 30 s with the identical delivery id, second attempt `success`; both attempts visible. |

Also covered: routing assignment + ack-deadline timer, permanent failure → fallback + `hub.delivery_failure` (+ `used_fallback` attempts, response excerpt, consecutive failures), closure to prior recipients + timer cancellation + coalescing of a stale `opened` row, ack-overdue escalation with next-step scheduling and no escalation once acknowledged, shadow integrations, webhook header/signature contract and response classification against a real Kestrel receiver, SMTP against an in-test SMTP sink, routing engine and escalation policy units, backoff schedule.

## Decisions made

- **One `cfg.policy` table** for all four policy kinds instead of one table per kind (05 §6 lists them separately; the columns are identical and the ★ one-active-version index is per (kind, id)). The API in 06 §4 addresses policies by kind anyway.
- **Routing is one document** (an ordered `rules` list) under a well-known id; 04 §7.1 evaluates a single ordered list.
- **Team destinations are the default delivery target** for a rule without explicit `destinations`; the fallback destination of a team destination is also a team destination, so both receive `episode.opened` when both are subscribed — configure the fallback's `event_types` narrowly if that is not wanted.
- **`episode.closed` goes to every destination that had a row staged** for the episode (not only successfully sent), so a receiver that is retrying still learns about the closure.
- **Fallback is one hop**: a fallback row never falls back again; the payload carries `_fallbackOf` (stripped before sending) so attempts are flagged `used_fallback`.
- **`external:<name>` escalation targets** resolve to an active destination with that name; if none exists the step is recorded on the timeline and audit as escalated "to external" without a send — C7 must supply the real target before pilot.
- **Secrets at rest** use ASP.NET Data Protection with a file-system key ring at `DataProtection:KeysPath`; without it keys are ephemeral (tests/dev). The Key Vault-backed key ring is a Helm/CSI concern (ADR-11).
- **Timeout ⇒ `response_lost`**, connection/DNS errors ⇒ `retryable`: only the former may have reached the receiver.
- **Users are not in this slice**: `user:<id>` targets and `owner_user_id` are stored but resolve with milestone 4's user model.
