# 04 — Domain Model, State Machines and Algorithms

Everything here is normative for `DeNoise.Domain` and `DeNoise.Application`. Spec references are to `spec/denoise-spec-v0.4.md`.

## 1. Aggregates

| Aggregate | Root | Contains | Invariant owner for |
|---|---|---|---|
| Integration | `Integration` | mapping versions, coverage config, ingest credentials | who may send, how it is interpreted |
| Episode | `Episode` | episode_events (applied), acknowledgements, notes, timers | condition/handling state, version |
| AlertIdentity | value object | fingerprint + components | identity equality |
| Heartbeat | `Heartbeat` | recent runs ring, token | schedule state |
| Group | `AlertGroup` | member episode ids | group severity |
| Policy set | `RoutingPolicy`, `EscalationPolicy`, `LifecyclePolicy`, `SuppressionPolicy` | versions | activation |
| Team | `Team` | members, destinations, fallback, coverage hours | ownership resolution |
| Destination | `Destination` | channel, secret, fallback | delivery target |

Raw events, applied events, audit entries, outbox rows, delivery attempts and jobs are **records**, not aggregates — append-only with no domain behaviour.

## 2. Episode state machine

Two independent dimensions plus suppression. `version` increments on every persisted change.

### 2.1 Condition state

| From | Event | Guard | To | Side effects |
|---|---|---|---|---|
| — | `firing` (effective, new identity or all prior episodes closed) | ordering check passes | `firing` | create episode, `first_seen=last_seen=occurred_at`, count=1, handling=`new`, schedule `ack_deadline` if actionable, schedule `auto_resolve` per policy, outbox `episode.opened` |
| `firing` | `firing`/`update` (effective, same identity) | `occurred_at ≥ last_seen − skew` | `firing` | `last_seen=max`, count++, severity=max(policy), reschedule `auto_resolve`; outbox only on material severity increase (§6) |
| `firing` | `resolved` (effective) | ordering check passes | `resolved` | closure_reason=`source_resolved`, evidence=`source`, handling→`closed` unless policy `keep_open_on_resolve`, cancel timers, outbox `episode.resolved` |
| `firing` | verification success | scheduled `verify` job returned "not active" | `resolved` | reason `verified_resolved`, evidence `api_verification` |
| `firing` | inactivity deadline | guards in §5.3 | `resolved` | reason `inactivity_timeout`, evidence `heartbeat_and_inactivity` or `inactivity_unverified` |
| `firing` / `unknown` | administrative expiry | severity/policy permits | unchanged (`firing`/`unknown` preserved) | handling→`closed`, reason `expired_unverified`, evidence `none` |
| `firing` | `cancelled` | — | `not_applicable` | reason `source_cancelled` |
| `firing` | coverage → `degraded`/`unavailable` | episode's integration | `unknown` | suspend `auto_resolve` job (status `suspended`), UI badge |
| `unknown` | coverage → `healthy` | — | `firing` | resume job with deadline recomputed from `last_seen` |
| `unknown` | `firing` (effective) | — | `firing` | as update |
| `resolved` (closed) | `firing` (effective, `occurred_at > closed_at`) | not replay | **new episode** | link `previous_episode_id`; old untouched |
| any | `informational` | — | `not_applicable` | handling `new`; timer `informational_expiry` |

`heartbeat` event type never touches episodes; it updates coverage (§9) only.

### 2.2 Handling state

| From | Action | Actor | Guard | To | Side effects |
|---|---|---|---|---|---|
| `new` | acknowledge | user | user has `episode.ack` in scope | `acknowledged` | `assignee=actor` if unassigned, `acknowledged_at`, cancel `ack_deadline`, schedule `follow_up` if policy, audit, outbox `episode.acknowledged` |
| `new`/`acknowledged` | assign(team, user?) | user | `episode.assign` | unchanged | reassignment does **not** reset `ack_deadline` or `follow_up` |
| `acknowledged` | take over | user ≠ assignee | `episode.ack`, explicit `force=true` | `acknowledged` | audit `takeover`, notify previous assignee |
| `new`/`acknowledged` | manual close(reason text) | user | `episode.close`, reason non-empty | `closed` | closure_reason `manual_close`, evidence `human`, condition unchanged, cancel timers |
| any open | condition→resolved | system | — | `closed` (default) | — |
| `closed` | restore for review | user | `episode.restore`, closure_reason ∈ {`expired_unverified`,`manual_close`} | `acknowledged` | condition unchanged (still last-known), `restored_from_reason`, no recurrence count, no `episode.opened` notification |
| any | add note | user | `episode.note` | unchanged | audit |

Closed episodes are immutable except `restore` and notes.

### 2.3 Suppression

`suppressed_until`, `suppression_source ∈ {silence, maintenance}` on the episode; computed at evaluation time from active `SuppressionPolicy` rows matching the episode's scope predicate. Suppression blocks **outbox creation** (row created with status `suppressed`, later coalesced) — never ingestion, transitions, or audit.

## 3. Delivery idempotency

`delivery_key` (text, ≤ 512) per integration:

1. If `source_event_id` mapped and non-empty → `evt:{source_event_id}`.
2. Else if `source_alert_id` and `source_version` → `ver:{source_alert_id}:{source_version}`.
3. Else if `source_alert_id`, `event_type`, `occurred_at` (mapped, reliable) → `ts:{source_alert_id}:{event_type}:{occurred_at ISO ms}`.
4. Else → `body:{sha256(canonical(body minus mapping.ignore_paths))}` and set `identity_confidence=body`.

Unique index `(integration_id, delivery_key)` on `alert.applied_event`. Insert conflict ⇒ duplicate: record in `alert.delivery_duplicate` (counter table), no other effect. Integration profile flag `retransmission_indistinguishable=true` disables the "occurrence count is exact" badge in UI.

## 4. Fingerprint

Inputs from the mapping's `identity:` block (ordered list of `{name, path}`).

Canonicalisation:
1. Take each identity component value; `null`/missing ⇒ **mapping exception** unless the component is marked `optional: true`, in which case literal `∅`.
2. Strings: Unicode NFC, trim, **no** case folding unless component `case_insensitive: true`.
3. Dimensions object: keys sorted ordinal, values stringified as in 2, serialised `k=v` joined `;`.
4. Components serialised as `name=value` joined with `\u001F`, prefixed by `integration_id` and `mapping.identity_version`.
5. `fingerprint = sha256(utf8(string))`, hex lowercase.

Persist `identity_components jsonb` alongside for explainability. `identity_version` increments only when the identity block changes; a change requires the migration strategy in spec §10.2 (new episodes only, no rehashing of open ones).

## 5. Ordering

Per source instance (`source_alert_id`):

- If `source_version` present: apply only if `> episode.last_applied_version`; else record as `late` in `episode_event`, no state change.
- Else: apply only if `occurred_at ≥ episode.last_seen − clock_skew_tolerance` (default 120 s). A `resolved` with `occurred_at < last_seen` of a `firing` in the current episode is `late`.
- A `resolved` arriving with **no** open episode for the identity creates a `closed_source_instance` marker (`alert.source_instance_state`) with `resolved_at`. A later `firing` for the same `source_alert_id` with `occurred_at ≤ resolved_at` is `late`; with `occurred_at > resolved_at` opens a new episode.
- Replay mode `historical` sets `is_replay=true` on the job; replayed events are always recorded as `replayed` and never transition.

### 5.3 Auto-resolve guards (scheduler, before closing)

All must hold, evaluated inside the same transaction that closes:
1. `episode.version == job.expected_version` **or** `episode.last_seen == job.expected_last_seen` (a note or ack may have bumped version — allow, recompute).
2. `now ≥ last_seen + timeout` (recompute from current `last_seen`; if not yet due, reschedule).
3. Integration coverage state ∉ {`degraded`, `unavailable`} — if it is, set job `suspended`, condition→`unknown`.
4. No `normalise` job for this integration older than `processing_delay_threshold` (default 5 min).
5. Integration `auth_failing=false`, `mapping_failure_rate` below threshold.
6. Policy version still active and still has `auto_resolve.enabled`.
7. If `verify_before_close ∈ {api_if_available, api_required}` and integration has `state_query` capability: run it; `active` ⇒ reschedule (+ timeout); `not_active` ⇒ close with `verified_resolved`; `error` ⇒ `api_required` ⇒ suspend, `api_if_available` ⇒ continue with inference.
8. Severity ∈ {`critical`,`high`} and `escalate_before_close` ⇒ if no `stale_state_escalation` sent yet: send it, reschedule + `stale_escalation_lead` (default 30 min), do not close.

Evidence: coverage `healthy` throughout the silence window ⇒ `heartbeat_and_inactivity`; coverage `unknown` ⇒ `inactivity_unverified`.

## 6. Notification triggers

| Trigger | Condition | Outbox type |
|---|---|---|
| opened | new actionable episode, not suppressed | `episode.opened` |
| severity increase | new severity > previous **and** (new ∈ {high, critical} **or** handling=`acknowledged` and `now − acknowledged_at > follow_up_window`) | `episode.escalated_severity` |
| ack overdue | `ack_deadline` fires, handling still `new` | `episode.ack_overdue` (+ escalation step) |
| follow-up overdue | `follow_up` fires, handling `acknowledged`, condition still `firing` | `episode.follow_up_due` |
| resolved / cancelled / expired | closure with prior recipients | `episode.closed` |
| stale-state | §5.3 step 8 | `episode.stale_critical` |
| routing failure | no rule matched or destination invalid | `episode.routing_failure` → triage team |
| delivery failure | attempts exhausted | `hub.delivery_failure` → destination owner + hub ops |
| coverage lost / restored | §9 | `coverage.lost`, `coverage.restored` |
| heartbeat missed / recovered | §10 | `heartbeat.missed`, `heartbeat.recovered` |

Dispatcher relevance check before send: episode still open for `opened`/`escalated`; still `new` for `ack_overdue`; not superseded by a later outbox row for the same episode with a terminal type (coalesce ⇒ status `coalesced`).

## 7. Policies

### 7.1 Routing rule
```
priority: int (lower first)
match: <predicate> (07 §5)
team: team_id
destinations: [destination_id]          # optional; default team destinations
escalation_policy: id                   # optional; default team's
stop: bool (default true)
```
Evaluation: first matching rule wins unless `stop=false` (adds destinations, continues). No match ⇒ `team = fallback_triage_team`, flag `routing_correction_required=true`, trigger `episode.routing_failure`.

### 7.2 Escalation policy
```
ack_deadline: duration (e.g. 15m)      # null = no deadline
follow_up_window: duration | null
steps:
  - after: 0m      targets: [team_destinations]
  - after: 15m     targets: [user:…, destination:…]
  - after: 30m     targets: [external:oncall_bridge]   # §7.3 of spec, C7
repeat_last_step_every: duration | null
max_repeats: int (default 3)
business_hours_only: bool (default false)   # if true, deadlines pause outside team.coverage_hours
```

### 7.3 Lifecycle policy (per rule match)
See spec §12.3 YAML. Fields: `profile`, `expected_repeat_interval`, `delivery_grace`, `auto_resolve.{enabled, after_silence, verify_before_close, on_coverage_unknown, on_coverage_degraded, escalate_before_close_if_severity}`, `expiry.{informational_after, unknown_lifecycle_review_after, unverified_expire_after, never_expire_severities}`.

### 7.4 Grouping rule
```
match: <predicate>
key: [field paths]                      # e.g. [service, environment]
window: duration (fixed, from first member; default 10m)
notify: first_only | first_and_new_critical (default)
```
Group severity = max active member. Members keep their own timers and states. Groups never span `access_scope`.

### 7.5 Suppression (silence / maintenance)
```
kind: silence | maintenance
scope: <predicate>
starts_at, ends_at (RFC 3339) ; tz (IANA) for display and recurrence
reason: text (required) ; created_by
auto_pause_heartbeats: bool (default true)
```
On end: evaluate all open episodes matching scope; emit one `episode.summary_after_suppression` per team with currently actionable items.

## 8. RBAC matrix

Roles: `viewer`, `operator`, `integration_admin`, `platform_admin`. All checks also require the target's `access_scope` ∈ user's scopes (platform_admin: all scopes).

| Permission | viewer | operator | integration_admin | platform_admin |
|---|---|---|---|---|
| episode.read, history.read | ✓ | ✓ | ✓ | ✓ |
| episode.ack / assign / note / close / restore / silence | | ✓ | ✓ | ✓ |
| episode.bulk (≤ 200 items) | | ✓ | ✓ | ✓ |
| episode.raw_payload.read | | | ✓ | ✓ |
| heartbeat.manage (own scope) | | ✓ | ✓ | ✓ |
| integration.read | ✓ | ✓ | ✓ | ✓ |
| integration.manage, mapping.manage, replay.preview/retry | | | ✓ | ✓ |
| replay.historical | | | | ✓ |
| policy.manage (routing, escalation, lifecycle, grouping) | | | ✓ | ✓ |
| team.manage, destination.manage | | | ✓ | ✓ |
| suppression.create (≤ 24h) | | ✓ | ✓ | ✓ |
| suppression.create (> 24h) | | | ✓ | ✓ |
| audit.read | ✓ (own scope) | ✓ | ✓ | ✓ |
| hub.health.read | | ✓ | ✓ | ✓ |
| hub.admin (retention, failure queue, job control) | | | | ✓ |
| user.manage (create, roles, scopes, reset, disable, unlock) | | | | ✓ |
| me.tokens, me.sessions (own) | ✓ | ✓ | ✓ | ✓ |

## 9. Coverage state machine (per integration)

Inputs: managed canary events, bound heartbeat state, api probe results. Effective state = **worst** among configured methods that cover the alert path (api_probe never raises above `degraded` on its own).

| From | Signal | To |
|---|---|---|
| `unknown` | first success | `healthy` |
| `healthy` | no signal for `delayed_after` | `delayed` |
| `delayed` | signal | `healthy` |
| `delayed` | no signal for `alert_after` | `degraded` → emit `coverage.lost`, suspend auto-resolve jobs for integration |
| `degraded` | no signal for `unavailable_after` | `unavailable` (same episode, severity bump) |
| `degraded`/`unavailable` | `recovery_successes_required` consecutive successes | `healthy` → resolve coverage episode (`source`), resume suspended jobs |
| any | canary explicitly `/fail` or probe auth error | `degraded` immediately |

Coverage episode: identity `coverage:{integration_id}`, severity `high` by default, owner = integration owner team, not groupable, exempt from auto-resolve policies (resolves only via the machine above).

## 10. Heartbeat state machine

| From | Signal | To | Effect |
|---|---|---|---|
| `unknown` | first ping | `healthy` | — |
| `healthy` | now > expected_next | `late` | UI only |
| `late` | now > expected_next + grace | `missed` | open episode `heartbeat:{heartbeat_id}` with `severity_on_miss`, owner team |
| `missed` | ping (×`recovery_successes_required`) | `healthy` | resolve episode (`source`), `heartbeat.recovered` |
| any | `/fail` or non-zero `/exit` | `missed` | immediate |
| any | pause(actor, reason) | `paused` | close open miss episode as `manual_close`; report as coverage gap |
| `paused` | resume | `unknown` | expected_next recomputed from now |
| `healthy`/`late` | maintenance window covering scope begins | `paused` (auto) | resume at window end |

`expected_next`: interval schedules ⇒ `last_ping + interval`; cron ⇒ next occurrence after `last_ping` in `schedule_tz` (Cronos, handles DST). Missed detection runs in the scheduler every 30 s over `hb.heartbeat WHERE state IN ('healthy','late') AND expected_next + grace < now`.

## 11. Timer catalogue (`ops.job.kind`)

`normalise`, `auto_resolve`, `verify_state`, `ack_deadline`, `follow_up`, `escalation_step`, `informational_expiry`, `stale_review`, `admin_expiry`, `group_window_close`, `suppression_end`, `coverage_check`, `heartbeat_check`, `api_probe`, `retention_raw`, `retention_rows`, `deadman_ping`, `replay`.

Every job row: `expected_version` (nullable), `expected_last_seen` (nullable), `payload jsonb`, `not_before`, `attempts`, `max_attempts`, `reserved_by`, `reserved_until`, `status ∈ {pending, reserved, done, failed, suspended, cancelled}`, `last_error`.
