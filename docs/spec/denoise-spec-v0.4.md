# DeNoise — Product and Architecture Specification

**Version:** 0.4 (consolidates drafts 0.2 and 0.3)
**Status:** For decision — not yet approved for implementation
**Author:** Sylwester Grabowski, Shared Services, SoftwareOne
**Date:** 11 September 2026

**Blocking condition:** Section 6 is a decision gate. No implementation work beyond the measurement task in Section 3.3 should start until that gate is closed.

---

## 1. Purpose and audience

This document defines a bounded first release of DeNoise: a central operational workspace for receiving, normalising, deduplicating, owning, tracking and delivering alerts from heterogeneous monitoring systems.

| Audience | What they should take from this document |
|---|---|
| CTO / CPTO | Sections 3, 4, 6, 21 — problem, measures, build-versus-adopt, exit criteria |
| Engineering | Sections 8–19 |
| Operators and team leads | Sections 7, 11, 14 |
| Security and data protection | Sections 18, 19 |

All numeric values are proposals. Every one of them must be re-derived from the measured baseline in Section 3.3 before it becomes a commitment.

---

## 2. Definitions

Terms used with a specific meaning throughout.

| Term | Definition |
|---|---|
| **Actionable alert** | An alert whose configuration declares that a human must decide something. Informational and heartbeat events are not actionable and are exempt from ownership and acknowledgement requirements. |
| **Effective signal** | An accepted event that passed delivery-idempotency checks and therefore represents new information about a condition. Retries are not effective signals. |
| **Alert identity** | The stable identity of a monitored condition, derived from configured identity fields. |
| **Episode** | One continuous occurrence of a condition under one identity. |
| **Condition state** | What DeNoise believes about the monitored system. |
| **Handling state** | What people have done about it. |
| **Coverage** | Verified ability to observe a source. Absence of alerts is not coverage. |
| **Registered heartbeat** | A dead-man's switch registered by a team through the UI or API. The producer calls DeNoise on a schedule; silence past the grace period raises an alert. |
| **Managed canary** | A synthetic alert rule provisioned in a source system by the platform team, firing on a fixed cadence through the real alert path, used to prove that detection and delivery still work end to end. |

### 2.1 Canonical severity scale

Severity is a closed enum. Source values map into it; unmapped values become `unknown`.

| Value | Meaning | Default behaviour |
|---|---|---|
| `critical` | Customer-visible loss or imminent loss of service | Immediate notification, escalation enabled, no automatic expiry |
| `high` | Significant degradation or a condition that will become critical | Immediate notification, escalation enabled, no automatic expiry |
| `medium` | Degradation requiring attention within the working day | Notification, no escalation by default |
| `low` | Hygiene or trend condition | Queue only |
| `informational` | Record of an event, not a persistent condition | Queue only, informational expiry applies |
| `unknown` | Source severity could not be mapped | Treated as `high` for routing; visibly flagged as unmapped |

`unknown` must never be silently downgraded to `informational`. That downgrade is the single most likely way for this product to hide a real outage.

---

## 3. Problem, evidence and baseline

### 3.1 Current state

Alerts travel directly from each source to destinations chosen independently by that source: MongoDB Atlas sends email, Azure Monitor sends email and webhooks, applications use assorted webhooks and shared mailboxes.

Consequences:

1. No single view of what is currently wrong.
2. Alerts in shared channels have no owner.
3. Repetition erodes trust in the signal.
4. No consistent acknowledgement or resolution lifecycle.
5. Delivery failures and monitoring gaps are invisible.
6. Silence is ambiguous: recovery or loss of monitoring.
7. Old alerts persist indefinitely or vanish without an auditable reason.

### 3.2 Weakness of the current problem statement

Every statement in 3.1 is currently an assertion. None is quantified. A proposal to build an internal platform cannot be evaluated against unquantified pain, and the performance and retention targets later in this document are guesses without it.

### 3.3 Required baseline measurement (prerequisite, ~2 weeks)

Before the decision gate in Section 6, collect for a representative 14-day window:

| Measure | Why it matters |
|---|---|
| Alert events per source per day, and per-minute peak | Sizing, retention, queue design |
| Distinct conditions per day versus total events | Establishes the real deduplication ratio — the primary claimed benefit |
| Proportion of events that were acted on at all | Establishes whether the problem is routing or volume |
| Median time from alert to human acknowledgement (where discoverable) | Baseline for the acknowledgement target |
| Number of distinct destinations (mailboxes, channels, webhooks) in use | Migration effort |
| Number of alerts that were missed and later surfaced by a customer or by another team | The only measure that justifies spend |
| Average and p99 payload size per source | Storage and retention model |

Deliverable: a one-page baseline sheet. If the deduplication ratio is near 1:1 and missed alerts are rare, the honest conclusion is that this product should not be built.

---

## 4. Goals and success measures

Six outcomes, each with a measure. The eighteen-item list in draft 0.3 was a feature list, not a goal list; it has moved into Section 5.

| # | Outcome | Measure | Target |
|---|---|---|---|
| G1 | Every actionable alert has an accountable team | Share of actionable episodes with a valid owning team | ≥ 98% |
| G2 | Noise falls without losing signal | Notifications delivered per distinct condition | ≥ 60% reduction versus baseline, with zero missed critical conditions |
| G3 | Status shown in the UI is trustworthy | Closure mix by evidence type | `source` / `api_verification` / `human` ≥ 70% at launch, rising as coverage methods are added; `inactivity_unverified` tracked and trending down; `expired_unverified` reported separately and never counted as recovery |
| G4 | Monitoring gaps become visible | Median time from loss of source coverage to a coverage alert | < 10 minutes |
| G5 | Response improves | Median time to acknowledgement for `critical` and `high` | Better than baseline; absolute target set after 3.3 |
| G6 | Operators actually use it | Share of acknowledgements performed in DeNoise rather than in email or Teams | ≥ 80% of pilot-team alerts after 8 weeks |

Raw closure count is explicitly not a success measure.

---

## 5. Scope

### 5.1 In scope for v1

- Operational web UI (this is a product capability, not a reporting layer).
- Integrations: Azure Monitor, MongoDB Atlas, generic authenticated JSON webhook.
- Declarative mappings with validation, preview, versioning and rollback.
- Alert identity, delivery idempotency, condition deduplication, episodes, lifecycle.
- Team ownership, individual assignment, acknowledgement, explicit audited takeover.
- Source-aware automatic resolution and administrative expiry with distinguishable evidence.
- Integration health, registered heartbeats (self-service via UI and API), managed canaries, and monitoring-coverage alerts.
- Notification delivery through **generic outbound webhooks** (templated, signed) and SMTP email. Teams, Slack, ticketing and automation platforms are webhook destinations, not built-in channels — see 16.4.
- Static escalation policies, bounded grouping, maintenance windows and time-limited silences.
- Enrichment with service, environment, owning team and runbook.
- Failed-event inspection and controlled replay.
- SSO, role-based access, audit history.
- Durable ingestion, transactional notification outbox, self-monitoring via an independent path.

Email ingestion is included only if a confirmed launch source cannot emit a webhook. It is not a launch prerequisite. If it is needed, it is a separate adapter with explicitly weaker identity guarantees.

### 5.2 Out of scope for v1

- Evaluation of metrics, logs or time series. Detection stays in the source systems.
- Automated root-cause analysis, remediation, or arbitrary user scripts.
- Full incident management, incident command, postmortems. The term *incident* is reserved.
- Native telephone paging.
- Guaranteed bidirectional state synchronisation with producers.
- Customer-facing multi-tenant SaaS operation.

### 5.3 Scope items that drafts 0.2 and 0.3 excluded and should not have

**On-call rotations and schedules.** Both drafts exclude rotations while simultaneously requiring that every actionable alert have an accountable owner and stating that a notification channel is not an owner. Those positions are contradictory outside working hours: without a schedule, `critical` at 02:00 resolves to a Teams channel, which the specification itself declares insufficient. See Section 7.3 for the resolution.

---

## 6. Decision gate: build versus adopt

### 6.1 Why this gate exists

Draft 0.2 evaluated exactly one alternative (Keep) and justified building in .NET on the basis of "team maintainability". That is not a build case. A build case needs a cost, an owner, an alternative set and a rejection reason for each alternative.

### 6.2 Cost of building (order of magnitude, to be refined)

| Item | Estimate |
|---|---|
| Initial build to the v1 scope in Section 5.1 | 2–3 engineers × 6–9 months |
| UI work included above | ~40% of that effort; the UI is most of this product |
| Steady-state ownership | ≥ 0.5 FTE indefinitely, plus on-call for the Hub itself |
| Hidden cost | DeNoise becomes a tier-0 dependency: if it is down, nobody learns that anything else is down |

Shared Services is ~30 people across Platform Engineering, QA and UX, supporting four products and 50+ microservices. Committing 2–3 engineers for two or three quarters to an internal alerting tool is a portfolio decision, not a technical one.

### 6.3 Alternatives that must be assessed

| Option | Strongest argument for | Strongest argument against | Must be checked |
|---|---|---|---|
| **Azure Monitor alert processing rules + action groups** | Already licensed and deployed; natively handles suppression, grouping and routing for the Azure estate at zero build cost | Does not cover Atlas or application webhooks; no cross-source queue or ownership model | What share of current alert volume is Azure-only? If it is most of it, the residual problem may not justify a platform |
| **Jira Service Management alerting / on-call** | Atlassian is already in the stack; Opsgenie's alerting and on-call features were folded into JSM, and Opsgenie itself reaches end of support on 5 April 2027, so JSM is the supported path | Licence cost per responder; opinionated model; Jira operational overhead | Whether existing SoftwareOne Atlassian licensing already covers the required tier |
| **Grafana IRM / OnCall** | Strong routing, schedules and escalation; self-hostable | Another platform to operate; licensing model has moved | Fit with existing Grafana usage, if any |
| **Keep** | Closest to this specification's shape; open source | Previously assessed as heavy to install; edition boundaries between MIT-licensed content and the `ee` directory must be checked feature by feature | Whether the heaviness objection is an install-time or an operate-time objection |
| **Build** | Exact fit; .NET maintainability within the team | Highest cost; permanent ownership; tier-0 risk | Only chosen if the alternatives fail the scenarios in Section 22 |

Opsgenie must not be selected as a new platform: new sales ended 4 June 2025 and support ends 5 April 2027.

### 6.4 Gate conditions

Build proceeds only if all of the following hold:

1. The baseline in 3.3 shows a deduplication ratio materially better than 1:1 **and** evidence of missed or unowned alerts.
2. No adopted option passes the acceptance scenarios in Section 22 without unacceptable compromise, evidenced by a dry-run, not by reading feature lists.
3. A named owning team and a named long-term maintainer exist.
4. The CTO explicitly accepts the steady-state cost in 6.2.

**Decision owner:** Sylwester Grabowski. **Decision date:** to be set; recommended within 30 days of the baseline sheet.

---

## 7. Users, ownership and coverage

### 7.1 Personas

| Persona | Responsibilities |
|---|---|
| Operator | Reviews, acknowledges, assigns and closes alerts |
| Team lead | Monitors workload, unassigned alerts, overdue response |
| Integration owner | Maintains credentials, scope, mappings, canary rules and health |
| Policy administrator | Maintains lifecycle, routing, grouping and suppression policies |
| Hub operator | Runs DeNoise and handles processing and delivery failures |
| Auditor / viewer | Reads alerts and history without modifying state |
| Product owner | Owns operational policy, adoption and the measures in Section 4 |

### 7.2 Ownership rules

Every actionable alert has an owning team, an optional assigned person, a configured fallback team, an acknowledgement deadline where required, and an escalation destination.

- An alert matching no ownership rule goes to the fallback triage team and is flagged as requiring routing correction. It is never discarded.
- Acknowledging an unassigned alert assigns it to the acknowledging user.
- Taking over from another user is explicit and audited.
- Reassignment does not silently restart an overdue response timer.
- A notification channel is not an owner.

### 7.3 Out-of-hours coverage (resolves the contradiction in 5.3)

v1 adopts the following, and states it plainly to stakeholders:

1. **DeNoise timers use elapsed time and are business-hours aware only through an explicit policy flag per team.** There is no availability inference.
2. **For `critical` and `high` alerts, v1 does not become the sole delivery path.** Existing out-of-hours routes remain live in parallel until a rotation capability exists.
3. **Team → person resolution out of hours is delegated,** not invented: the escalation destination for out-of-hours may be an external system that owns rotations (JSM/Opsgenie-successor, or a phone tree). DeNoise records that it escalated and to where.
4. **Rotations are a named v2 candidate** with an explicit trigger: the first out-of-hours `critical` that is acknowledged later than the agreed threshold.

Without point 2, this product creates an availability regression on its first night of operation.

---

## 8. Domain model

| Entity | Purpose |
|---|---|
| Integration | Configured source instance: credentials, scope, mappings, capabilities, health method |
| Raw event | Immutable received body and permitted request metadata |
| Normalised event | Interpreted source fact derived from a raw event |
| Alert identity | Stable identity of a monitored condition |
| Alert episode | One continuous occurrence of that condition |
| Alert group | Presentation and notification grouping of related episodes |
| Notification job | Durable request to send a lifecycle notification or escalation |
| Delivery attempt | Result of one attempt to contact a destination |
| Policy version | Versioned mapping, routing, expiry, grouping or suppression configuration |
| Audit entry | Record of a human or system action |
| Coverage alert | System-generated alert that a source can no longer be verified |

An episode stores: fingerprint and source-instance references; condition and handling state; first and latest effective signal times; latest receipt and verification times; owning team and assignee; acknowledgement and closure details; distinct occurrence count and separately recorded delivery duplicates; resolution evidence and closure reason; applicable inactivity policy and next deadline; notification, escalation and suppression state; and a version for optimistic concurrency.

---

## 9. Canonical event model

| Field | Description |
|---|---|
| `event_id` | Internal immutable event identifier |
| `integration_id` | Verified integration identity |
| `access_scope` | Server-assigned visibility boundary (see 19.2) |
| `source` | Producer type |
| `source_alert_id` | Producer alert-instance identifier |
| `source_event_id` | Producer event identifier, where available |
| `source_version` | Producer sequence or update version |
| `event_type` | `firing`, `update`, `resolved`, `acknowledged`, `cancelled`, `informational`, `heartbeat` |
| `occurred_at` | Source event time, where reliable |
| `received_at` | DeNoise receipt time |
| `severity` | Normalised severity (2.1) |
| `source_severity` | Original producer severity |
| `resource_id`, `resource_name` | Stable resource identity and display name |
| `rule_id`, `rule_name` | Stable source-rule identity and display name |
| `environment` | Production, staging, development or another configured value |
| `service` | Affected service |
| `summary`, `source_url` | Description and investigation link |
| `dimensions` | Identity-relevant dimensions |
| `labels` | Additional metadata |
| `raw_payload_id` | Reference to the raw request |
| `mapping_version` | Mapping version used during interpretation |

`first_seen`, `last_seen`, ownership, acknowledgement and closure belong to the episode, never to the event.

---

## 10. Identity, deduplication and ordering

### 10.1 Two distinct mechanisms

**Delivery idempotency** stops a producer retry from being applied twice. **Condition deduplication** consolidates distinct events about the same ongoing condition.

Prefer `source_event_id`. A source alert identifier alone is insufficient, because opening, updates and recovery often share it. Where no event identifier exists, use a documented, source-specific combination of instance identity, event type, version and a reliable occurrence timestamp.

Some producers make an identical retransmission indistinguishable from a genuine repeat. Where that is true, it must be recorded in the integration's capability profile and the UI must not promise an exact occurrence count.

A delivery duplicate must not increase the occurrence count, extend an inactivity timer, trigger a lifecycle transition, or generate a notification.

### 10.2 Fingerprint

Normally includes: integration ID; source scope (subscription, Atlas project); environment; stable resource identity; rule identity; relevant dimensions.

Normally excludes: severity; metric value; human-readable summary; receipt time.

Fingerprint components are stored alongside the hash so that a user can be shown *why* two events were combined. Missing identity fields produce a mapping exception or a configured fallback — never a merge under an empty key. Changing fingerprint rules requires an explicit migration plan.

### 10.3 Ordering

- Prefer producer sequence or version information; otherwise use source-instance identity and reliable occurrence times.
- An old recovery must not close a newer episode.
- A recovery received before its opening establishes a closed source instance; the delayed opening must not reactivate it.
- Replay records history without regressing current state and never creates an operational recurrence.
- Updating the episode, recording the applied event and creating notification jobs happen in one transaction.

---

## 11. Lifecycle

### 11.1 Independent dimensions

| Dimension | Values | Meaning |
|---|---|---|
| Condition state | `firing`, `resolved`, `unknown`, `not_applicable` | Knowledge about the monitored condition |
| Handling state | `new`, `acknowledged`, `closed` | Human handling |
| Suppression | Active policy and expiry | Whether notifications are muted |

Acknowledgement is not recovery. A condition may resolve before anyone acknowledges it. An episode may be expired while its last known condition was `firing`.

### 11.2 Closure reasons and evidence

| Reason | Meaning |
|---|---|
| `source_resolved` | Producer explicitly reported recovery |
| `verified_resolved` | A source-status query confirmed recovery |
| `inactivity_timeout` | Recovery inferred from silence under a validated repeating-source policy |
| `expired_unverified` | Administratively closed because the information became stale |
| `manual_close` | Authorised user closed it with a reason |
| `informational_completed` | A one-time informational event reached its expiry |
| `source_cancelled` | Producer invalidated the alert |

Evidence is recorded separately: `source`, `api_verification`, `heartbeat_and_inactivity`, `human`, `none`.

Administrative expiry is never displayed as confirmed recovery, and never rewrites a last-known `firing` or `unknown` condition as healthy.

### 11.3 Recurrence

A genuinely new firing signal after closure creates a new episode linked to the same identity, with a new acknowledgement requirement and new timers. Previous acknowledgement does not carry over. Previous episodes remain accessible.

---

## 12. Source profiles and automatic resolution

### 12.1 Profiles

| Profile | Producer behaviour | Resolution behaviour |
|---|---|---|
| `explicit_recovery` | Sends firing and resolved events | Source recovery, or API verification |
| `queryable_state` | Current state available via API | Reconcile through the source API |
| `repeating_while_active` | Repeats while the condition exists | May infer recovery after validated inactivity **and** healthy coverage |
| `one_shot` | An event, not a persistent condition | Close after an informational retention period |
| `unknown` | Contract not established | Never infer recovery; mark stale, expire only if policy permits |

Profiles are per rule, not per source. Different Azure Monitor rules legitimately need different profiles.

### 12.2 Automatic resolution is configured in DeNoise

Automatic closure is a core DeNoise capability, configured per rule **inside the Hub**. It is not inherited from the source, does not require the producer to change anything, and is **enabled by default** for every rule. The profile in 12.1 supplies a preset; every field of it is operator-overridable during onboarding.

This is deliberate. The reason alerts accumulate today is precisely that producers are unreliable about recovery: Atlas has no consistent recovery contract, an Azure `resolved` webhook can be lost in a delivery gap, and application webhooks have no contract at all. A system that closes an episode only when the source says so reproduces the current problem behind a nicer UI.

What the Hub must never do is present an **inferred** closure as a **confirmed** one. Source capability determines the evidence recorded, not whether closure happens at all.

| Evidence | When recorded | Presented as |
|---|---|---|
| `source` | Producer sent recovery | Confirmed recovery |
| `api_verification` | Source-state query confirmed recovery | Confirmed recovery |
| `heartbeat_and_inactivity` | Silence, with coverage verified healthy throughout | Inferred recovery, coverage verified |
| `inactivity_unverified` | Silence, coverage never established for this source | Inferred recovery, coverage **not** verified |
| `human` | Operator closed with a reason | Manual closure |
| `none` | Administrative expiry | Not recovery |

`inactivity_unverified` is the evidence level that makes this work on day one, before any canary rule exists. It is a weaker claim, shown as such, and reported separately in G3 — not a reason to leave the episode open forever.

Per-source presets, all overridable:

| Source | Preset profile | Auto-resolve default |
|---|---|---|
| Azure Monitor | `explicit_recovery` + `queryable_state` | On, as a backstop: close after 60 min of silence, preceded by a state re-query where the API is available |
| MongoDB Atlas | `queryable_state` | On: 30 min silence, reconciled against the Atlas Admin API before closing |
| Generic webhook, interval declared | `repeating_while_active` | On: `max(3 × declared interval + grace, 15 min)` |
| Generic webhook, interval undeclared | `unknown` | On: 60 min, evidence `inactivity_unverified` |
| Any rule at `critical` / `high` | Inherited | On, with a longer timeout and a stale-state escalation to the owning team **before** closure |

The Azure backstop matters more than it looks: a single dropped `resolved` webhook currently leaves an episode open indefinitely. The Hub timer is the only thing that fixes that, and it must not be disabled just because the source is nominally well-behaved.

### 12.3 Inactivity formula (where enabled)

```text
max(
  3 × maximum expected repeat interval + delivery grace,
  configured minimum inactivity period
)
```

Initial presets: delivery grace 5 minutes; minimum inactivity 15 minutes. Configurable per rule.

Example configuration:

```yaml
# Configured per rule in DeNoise; the source is not involved.
lifecycle:
  profile: repeating_while_active      # preset, overridable
  expected_repeat_interval: 1m
  delivery_grace: 5m

auto_resolve:
  enabled: true                        # default for every rule
  after_silence: 15m
  verify_before_close: api_if_available # none | api_if_available | api_required
  on_coverage_unknown: close_unverified # close_unverified | suspend
  on_coverage_degraded: suspend         # see 12.4
  escalate_before_close_if_severity: [critical, high]
```

Example timeline:

```text
10:00       First event; episode opened; opening notification sent
10:01–10:59 Repeats update last_seen and occurrence count only
10:59       Last effective signal
11:14       Episode automatically resolved after 15 minutes of silence
```

Required UI text:

> Automatically resolved after 15 minutes without another signal. Source delivery remained verified healthy throughout. Recovery was inferred from the configured repetition policy, not reported by the source.

### 12.4 When inference is postponed — and when it is not

Two situations look similar and must be treated differently:

| Coverage situation | Meaning | Default behaviour |
|---|---|---|
| **Never established** (`unknown`) | No coverage method has been configured for this source yet | Close on the timer, evidence `inactivity_unverified`, flagged in the UI. Silence has no alternative explanation, so withholding closure adds nothing but backlog. |
| **Was healthy, now failing** (`degraded`, `unavailable`) | Silence now has a competing explanation: the monitoring path broke | **Suspend** closure, raise a coverage alert, resume when coverage recovers |

Conflating the two was the flaw in 0.3: it suspended auto-resolution whenever a heartbeat was absent, which — given that neither launch source emits one — meant auto-resolution would never have run at all.

Closure is therefore postponed when: coverage was verified and has since degraded; source verification actively fails; integration authentication fails; mapping failures affect the source; accepted events are unprocessed; processing delay exceeds threshold; or a maintenance action makes the signal unreliable. Each of these is an active failure signal, not an absence of configuration.

Before applying a scheduled resolution the worker rechecks episode version, latest effective signal, integration health and active policy. A stale timer cannot close an updated episode.

### 12.5 Administrative expiry

| Type | Proposed default |
|---|---|
| Informational / one-shot | Close after 24 hours |
| `medium` or `low` with unknown lifecycle | Review after 24 hours; expire after 7 days if policy permits |
| `critical`, `high`, `unknown` severity | No automatic expiry; stale-state escalation to the owning team instead |

Expired episodes leave the active queue, remain searchable, appear in the stale/expired view, retain their last known condition, and feed unverified-closure reporting.

Policy changes must preview their effect on existing episodes before activation and must never silently close a backlog.

---

## 13. Coverage and integration health

### 13.1 What must be distinguishable

- No condition is active.
- The monitored thing failed.
- The producer failed.
- The delivery path failed.
- DeNoise processing failed.

Absence of alert traffic proves none of these.

### 13.2 Heartbeat is a first-class DeNoise capability

Neither Azure Monitor nor Atlas emits a heartbeat through the alert path. DeNoise therefore **provides** the heartbeat rather than waiting for one. Two mechanisms, deliberately different in what they prove:

| | **Registered heartbeat** (§13.3) | **Managed canary** (§13.4) |
|---|---|---|
| Defined by | Any team, through the UI or the public API | Platform team, centrally, as part of integration onboarding |
| Direction | Push: the producer calls DeNoise on a schedule | Pull-through: a synthetic rule in the source fires through the real alert path |
| Proves | The producer job is alive and reaching the Hub | The source is still evaluating rules **and** delivering them |
| Typical use | Cron jobs, backups, exporters, custom applications, in-house services | Azure Monitor scopes, Atlas projects, any centrally onboarded source |
| Coverage scope | That one job | The whole integration or action group |
| Setup cost per user | Two minutes, self-service | Zero — it exists the moment the integration is onboarded |

Both feed the same coverage state machine (§13.5) and the same evidence model (§12.2). A third, weaker signal — an active API probe from the Hub to the source — proves credentials and connectivity only, and never justifies inferring recovery on its own.

| Integration | Coverage method for v1 |
|---|---|
| Azure Monitor | Managed canary: a scheduled log-query alert rule that always fires, routed through the same action group as production alerts. Proves evaluation, action group and delivery together. |
| MongoDB Atlas | Managed canary where a low-noise synthetic alert can be configured, plus a scheduled Atlas Admin API poll for state reconciliation. The poll alone does **not** prove the webhook path. |
| Generic webhook / in-house services | Registered heartbeat, self-service |
| DeNoise itself | Internal worker heartbeats, plus an external dead-man's switch on a third-party service (§13.7) |

A plain endpoint ping proves reachability only. It is never sufficient on its own to infer recovery.

### 13.3 Registered heartbeats (self-service, API or UI)

A dead-man's switch. A team registers a heartbeat, receives an endpoint, and has its job call that endpoint on a schedule. Silence past the grace period raises an alert like any other.

#### 13.3.1 Object model

| Field | Description |
|---|---|
| `heartbeat_id` | Stable identifier |
| `name`, `description` | Human-readable |
| `owning_team`, `assignee` | Ownership, identical to any other alert (§7.2) |
| `access_scope` | Product boundary (§19.2) |
| `schedule` | Either `interval` (e.g. `5m`) or `cron` with a required `timezone` |
| `grace_period` | Lateness tolerated before the state changes |
| `severity_on_miss` | Canonical severity (§2.1) of the alert raised |
| `routing_policy` | Which notification and escalation policy applies |
| `binds_to_integration` | Optional: declares this heartbeat as the coverage signal for an integration or rule set |
| `recovery_successes_required` | Consecutive pings required to close the miss alert; default 1 for interval schedules, 2 where flapping is observed |
| `auto_pause_during_maintenance` | Suppress during maintenance windows covering its scope; default true |
| `state` | `healthy`, `late`, `missed`, `paused`, `unknown` |
| `last_ping_at`, `last_ping_source_ip`, `last_run_duration` | Observability |
| `token`, `token_rotated_at` | Ping credential |

#### 13.3.2 Ping API

```text
GET|POST  /hb/{token}              signal success
POST      /hb/{token}/start        job started — enables duration tracking
POST      /hb/{token}/fail         job failed explicitly; raises immediately, no waiting
POST      /hb/{token}/exit/{code}  non-zero exit code is treated as /fail
```

- `GET` must be supported. The dominant caller is `curl -fsS --retry 3` at the end of a shell script, and requiring `POST` or a JSON body causes teams not to adopt it.
- Optional request body up to a configured size is stored as the last run's output — the last few lines of a failing job are usually the whole diagnosis.
- Responses are `200` with a minimal body. The endpoint never returns application data.
- Rate-limited per token. The token lives in the URL, so: it is high-entropy, rotatable without re-registering the heartbeat, scoped to exactly one heartbeat, and write-only. It is a credential and must be treated as one in the UI.
- `/start` and `/fail` are optional; a heartbeat that only ever receives success pings is still valid.

Management API (`/api/v1/heartbeats`) supports create, read, update, delete, pause, resume and token rotation, with the same authentication and RBAC as the rest of the application API. Everything available in the UI is available through the API, and heartbeats are declarable as configuration so they can live in a team's repository alongside the job they watch.

#### 13.3.3 Semantics

- **Missing is detected by the scheduler, not by the ping.** A heartbeat with no traffic at all still transitions. This makes the Hub's own scheduler a dependency of the whole coverage model — hence §13.7.
- **Pings are state updates, not alert events.** They do not create episodes, do not enter the raw-event store at full retention, and do not count toward alert volume. Persist the last ping timestamp, counters, and a bounded ring of the last N runs (proposed: 100) with their bodies and durations.
- **A missed heartbeat creates one ordinary alert episode** with the same identity, ownership, acknowledgement and closure semantics as any other alert. It resolves with evidence `source` when a ping arrives — this is genuine confirmed recovery, not inference.
- **Cron schedules use wall-clock time in the declared timezone.** A job scheduled for 02:30 does not alarm on the night it does not exist, and does not double-alarm when the hour repeats.
- **Late versus missed:** `late` is informational and visible in the UI; `missed` (past grace) raises the alert. Only one alert per outage, regardless of how many intervals are skipped.
- **Paused heartbeats are visibly paused** and appear in a separate list with the actor and reason. A permanently paused heartbeat is a coverage gap and is reported as one.
- **A heartbeat bound to an integration** drives that integration's coverage state, and its miss alert carries the list of episodes whose automatic closure is suspended as a result (§12.4).

### 13.4 Managed canaries (centrally configured sources)

For sources onboarded centrally, coverage must not depend on any team remembering to add a heartbeat. Onboarding an integration provisions its canary automatically.

| Source | Canary |
|---|---|
| Azure Monitor | A scheduled log-query alert rule, provisioned as code per scope, whose query always returns a result, routed through the **same action group** as production alerts. Firing on a fixed cadence proves the evaluation engine, the action group and the webhook delivery path. |
| MongoDB Atlas | A synthetic alert on a condition the platform team controls, delivered through the same webhook integration; where no low-noise condition exists, fall back to Admin API reconciliation plus a registered heartbeat on the poller itself. |
| Future sources | The onboarding flow does not complete without a declared canary or an explicit, owner-signed exception recorded against the integration. |

Canary alerts:

- Map to `event_type: heartbeat`, never to an actionable alert. They never appear in the work queue.
- Feed the integration's coverage state directly.
- Are excluded from alert-volume metrics and from grouping.
- Have their cost and evaluation-noise measured during onboarding — a per-minute log-query alert rule across many scopes is not free, and the cadence should be the longest one that still meets the G4 detection target.

Canary cadence must be longer than the source's own evaluation granularity. A one-minute expectation against a five-minute evaluation window produces permanent false coverage alarms and trains people to ignore them.

### 13.5 Health states

| State | Meaning |
|---|---|
| `healthy` | Signals arrive within the expected interval |
| `delayed` | Late but below alert threshold |
| `degraded` | Coverage uncertain |
| `unavailable` | Coverage considered lost |
| `unknown` | Not yet established |

```yaml
# Coverage config on an integration — either method, or both.
coverage:
  methods:
    - type: managed_canary          # §13.4
      expected_interval: 5m
      delayed_after: 10m
      alert_after: 15m
      unavailable_after: 30m
      recovery_successes_required: 3
    - type: registered_heartbeat    # §13.3
      heartbeat_id: hb_atlas_poller
    - type: api_probe               # supporting only; never sufficient alone
      interval: 5m
on_failure:
  suspend_inactivity_resolution: true
  notify: [integration_owner, platform_operations]
```

Where several methods are configured, the integration is as healthy as its **weakest** method that covers the alert path. An API probe succeeding while the canary is silent is `degraded`, not `healthy`.

### 13.6 Coverage alert

One deduplicated episode per integration outage, containing: integration and affected scope; last successful coverage signal; last successfully processed alert; failed checks; authentication, mapping and backlog state; number of episodes whose automatic resolution is suspended; integration owner and runbook.

> Monitoring coverage lost for Azure Monitor Production.
> Last verified signal: 10:00 UTC.
> Current alerts may be stale. Silence-based resolution is suspended for 14 episodes.

It is assigned to the integration owner or platform team, escalates on its own policy, stays one episode for the whole outage, resolves after the configured number of consecutive successes, and never claims that the monitored applications are failing.

### 13.7 Self-monitoring

DeNoise can report source failures only while its own scheduler, database and notification path work. An external system must independently check ingestion health, application API health, database availability, queue lag, scheduler operation and dispatcher operation — over an independent notification path.

**DeNoise must never be the only mechanism that can report its own failure.** This is a launch blocker, not a nice-to-have, and it is the direct consequence of making DeNoise a tier-0 dependency.

---

## 14. User interface

### 14.1 Principles

An operational workspace, not a dashboard. It must make the next action clear; show ownership and deadlines; separate condition from handling; show whether information is current; expose uncertainty and coverage gaps; explain automatic decisions in plain language; and support triage without reading raw JSON.

### 14.2 Screens

| Screen | Purpose |
|---|---|
| Work queue | Decide what needs attention: filter, sort, group, acknowledge, assign, open |
| Alert detail | Investigate one episode: timeline, runbook, source link, ownership, lifecycle, delivery history |
| Team overview | Unassigned work, overdue response, ageing acknowledgements, stale alerts |
| Integrations | Setup, health, last successful processing, errors, canary status |
| Heartbeats | Register, edit, pause and rotate tokens; state, last ping, last run output and duration; which integration each one covers |
| Policies | Routing, mapping, expiry, grouping, maintenance, silences — with preview and version history |
| History | Search resolved, manually closed, inferred and expired episodes |
| Hub health | Backlog, parse failures, delivery failures, degraded components |

### 14.3 Work queue

Default views: Needs attention; My alerts; My teams; Unassigned; Acknowledged and active; Stale or unverified; Suppressed; Closed/history.

Each row: severity, summary, resource/service, environment, condition state, handling state, owner, age, latest evidence time. Where applicable: acknowledgement overdue, next escalation, automatic closure deadline, suppression expiry, delivery failure, stale information.

Sorting is deterministic; unacknowledged `critical` and overdue handling sort first; `unknown` severity stays prominent. Saved filters and shareable URLs are included.

Every entry point into the queue (rail, login redirect, "back to queue" links) opens it filtered to `environment ∈ {production, unknown}` (§15.5): operators see production plus anything the mapping could not classify, and switch to other environments or "all" with one click. The filter is ordinary URL state — a shared link or saved filter without `environment` shows every environment.

A quiet queue is presented as healthy **only** when the relevant integrations are verified healthy. Otherwise a scoped banner states which coverage is missing.

### 14.4 Actions

Acknowledge and take ownership; assign or transfer; add an operational note; set a time-limited silence; close manually with a reason; request a source-status refresh where supported; restore an administratively closed episode for review.

Restoring for review does not claim the source fired again and does not increment recurrence statistics. Actions show success only after server confirmation. Concurrent edits are detected and the user sees current state rather than overwriting a colleague. Bulk actions show the exact selection and remain permission-checked and audited per episode.

### 14.5 Onboarding flow

Source type and owner → credentials and scope → sample payload and mapping → identity preview → lifecycle and repetition profile → severity and enrichment → destination and fallback routing → inactivity/expiry policy → **coverage method and canary configuration** → dry run → activation.

The coverage step is always presented and its consequence is stated in the flow: an integration activated without a coverage method runs in `unknown` health, and its automatic closures carry `inactivity_unverified` evidence until a method is added. Activation is not blocked.

The auto-resolve step shows a plain-language preview: *"Episodes matching this rule will close automatically after 30 minutes without a signal. Because no coverage check is configured, those closures will be marked as unverified."*

### 14.6 Interaction quality

Responsive for desktop and mobile triage; keyboard access and visible focus; status conveyed by text and icon as well as colour, meeting WCAG 2.1 AA; predictable navigation with preserved filters; live updates that preserve selection and scroll position; visible last-update time and connection state; clear partial-failure and empty states; no reliance on an open browser tab for timers or processing.

UI language: English. Polish localisation is out of scope for v1 and should be confirmed with the pilot team rather than assumed.

---

## 15. Ingestion, mapping and enrichment

### 15.1 Ingestion contract

Each integration has a distinct identity and credentials. Public source endpoints are separated from authenticated operator APIs.

1. Authenticate the integration and enforce request limits.
2. Persist the raw body, permitted metadata and processing work atomically.
3. Return HTTP success only after the transaction commits.
4. Interpret and process asynchronously.

Store bytes or text; do not require valid JSON for initial storage. Invalid authentication is rejected. Authenticated payloads that fail interpretation remain recoverable within size and retention limits. If durable acceptance fails, return a source-compatible failure response — never acknowledge an event that exists only in memory.

### 15.2 Declarative mapping

v1 supports: field/path selection; constants and defaults; lookup tables; type and timestamp conversion; first-non-empty selection; limited conditional mapping; defined array handling; required-field validation; deterministic fingerprint construction.

Mappings are immutable versions with samples and expected outputs, deployable without a code release. New protocols, authentication mechanisms or transformations outside this vocabulary require adapter code — that boundary must be stated to stakeholders, because "no redeployment needed" will otherwise be over-promised.

### 15.3 Source-specific notes

- **Azure Monitor:** prefer the Common Alert Schema where supported, validating the metric, log and activity-log variants separately. Preserve alert instance identifiers. Distinguish condition changes from user responses. Azure webhook retries are finite and endpoints enter a cooldown after repeated failures — see 17.4.
- **MongoDB Atlas:** use native webhooks, verify the configured signature, and interpret headers as well as body. Severity is not present in the standard payload and must be supplied locally.
- **Email (only if required):** a separate adapter with explicit parsing confidence, weaker identity and lifecycle guarantees, preservation of the original message, and a review queue for uncertain parses.

### 15.4 Enrichment

Enrichment supplies service, environment, owner and runbook from local mappings and cached metadata. External enrichment calls must not block durable ingestion. Enrichment failure never drops an alert: ownership falls back to triage with the missing metadata visible. Security scope derives from the authenticated integration, never from payload labels.

### 15.5 Environments

DeNoise receives alerts from **every environment that a team is accountable for** — production, staging, development or another configured value — but treats them differently by policy, never by refusing them at ingest. One store for all environments is what makes deduplication, coverage, history and "this fired on staging before it fired on production" possible; the cost is volume (non-production typically produces several times the production alert count), which the rules below keep out of operators' way.

1. **Environment is a canonical field, not a source property.** It is derived by the mapping (`environment` field, lookup or `regex_map` over subscription or project identifiers), enters the fingerprint, and is available to every predicate: routing, lifecycle, grouping, suppression scope, `applies_when`. A mapping that cannot derive it yields `unknown`; the value is never silently guessed.
2. **One integration per environment where the source allows it.** Each integration has its own credentials, rate limit, `access_scope`, coverage method and shadow flag, so a chatty development subscription cannot exhaust a production budget and cannot see or route into production scope. Deriving the security scope from a payload label is forbidden (15.4); the same holds for environment when it decides visibility.
3. **Differentiate by policy.**
   - Production: full path — team routing, escalation, notifications.
   - Staging / UAT: routed to the team's own destination without escalation (or visible in the queue only, `notify: first_only`), shorter inactivity and expiry timers.
   - Development / sandbox: a **separate integration in shadow mode** — full processing, zero outbox — so mappings and noise can be studied without waking anyone.
   - Ephemeral environments (pull-request previews, per-developer stacks) are **not ingested**: every instance is a new resource identity, deduplication cannot help, and coverage would keep reporting a "silent source" for stacks that were simply deleted.
4. **The default view is production plus unclassified.** The queue opens on `environment ∈ {production, unknown}` (14.3). `unknown` — and an empty value — is included on purpose: an alert that lost its environment in mapping must be *more* visible, not hidden. Non-production views are one click away and shareable.
5. **Retention and targets are measured per environment.** Volume and noise figures (3.3, 20.1) are reported with an environment dimension so that development churn does not distort the production baseline.

---

## 16. Routing, notification, escalation, suppression

### 16.1 Routing

Routing selects the accountable team first, destinations second. Rules have explicit priority and match behaviour, and the UI shows which rule applied and why.

### 16.2 Notification triggers

New actionable episode; material severity increase; acknowledgement deadline exceeded; follow-up deadline exceeded; recovery, cancellation or administrative expiry where relevant to prior recipients; routing or delivery failure; loss of integration coverage.

Routine repeats update the episode silently. Acknowledgement stops acknowledgement escalation but does not imply repair; long-running acknowledged alerts remain visible and may carry a follow-up deadline.

Notification jobs store episode, transition, destination and policy version. The dispatcher rechecks relevance before sending, and coalesces or cancels messages for obsolete transitions — an old firing notification must never arrive after recovery as if current.

### 16.3 Grouping and suppression

Grouping uses explicit keys and a bounded time window. Children keep their own lifecycles; groups never cross access boundaries; closing one child does not close others; group severity reflects the most severe active child; a new `critical` child stays visible and may notify independently. Grouping never claims a shared root cause.

Maintenance windows and silences suppress delivery only — not ingestion, ownership, or history. They require scope, reason, actor and expiry, and must specify a time zone explicitly (DST transitions are an acceptance case). At expiry the system re-evaluates active alerts and emits a current summary; it does not replay muted notifications.

### 16.4 Delivery mechanism: outbound webhooks

DeNoise does not carry vendor-specific channels in v1. It has exactly two delivery channels:

1. **Outbound webhook** — an HTTP request to a destination URL with a templated body, configurable headers, an HMAC signature, and a per-delivery id for receiver-side deduplication.
2. **SMTP email** — for destinations that cannot accept HTTP.

Everything else is a webhook destination with a template: Microsoft Teams (via a Power Automate Workflows HTTP trigger, since Office 365 connectors were retired in May 2026), Slack, Jira, PagerDuty-style systems, or an automation platform that itself sends email. DeNoise ships **built-in templates** for the common receivers (generic JSON, Teams Adaptive Card for Workflows, Slack Block Kit, plain text) and lets an administrator write new ones; adding a receiver never requires a code release.

Why this rather than a Teams channel: the Hub's job is to decide *what* and *whom* to notify with a trustworthy state; the receiver ecosystem changes faster than this product will, and the O365 connector retirement is the most recent proof. A webhook with a template moves that churn into configuration.

Rules that keep this safe:

- Every destination has a **mandatory fallback destination**; a permanent failure (4xx other than 408/429) routes the notification to the fallback and raises a delivery-failure alert.
- Outbound bodies are rendered by a **logic-limited template engine**, not scripts; templates are validated and previewed against a sample before activation.
- Every request carries `X-DeNoise-Delivery-Id` (stable across retries) and an HMAC signature so receivers can verify and deduplicate.
- Destination URLs and headers are secrets: encrypted at rest, masked in the UI, never logged.
- A destination can subscribe to a subset of event types (opened, escalated, closed, …), so a ticketing receiver is not flooded with acknowledgements.

---

## 17. Architecture and reliability

### 17.1 Components

| Component | Responsibility |
|---|---|
| Web UI | Operational interaction |
| Application API | Queries, operator actions, policy management, permissions |
| Ingestion API | Authentication and durable acceptance |
| Processing workers | Normalisation, ordering, deduplication, lifecycle, routing |
| Scheduler workers | Escalation, verification, suppression expiry, inactivity |
| Notification dispatcher | Outbox consumption, retries, delivery results |
| PostgreSQL | Durable events, state, jobs, policies, audit |

A modular application with separately scalable API and worker deployments. Microservices are not required for v1. Backend: ASP.NET Core and .NET workers. UI: React and TypeScript against the authenticated application API — confirm against the maintained company frontend standard before implementation.

### 17.2 PostgreSQL as queue

PostgreSQL is the initial durable queue; workers claim work in short transactions using `FOR UPDATE SKIP LOCKED`, the pattern PostgreSQL documents for multiple consumers of a queue-like table. Long processing uses expiring reservations with ownership tokens; expired work is reclaimable and an obsolete worker cannot commit after losing its reservation. Jobs track attempts, next-attempt time and last error; permanently failing events move to a visible failure queue.

**Exit criterion:** if the measured baseline shows sustained throughput above roughly 1,000 events/minute, re-evaluate against a dedicated broker before build. The queue design must not be defended past the point where it stops fitting.

### 17.3 Transactional outbox

Episode transition and notification jobs commit in one transaction. Delivery happens outside database transactions and results are recorded. Destination idempotency is used where supported. Duplicate external delivery remains possible when a destination accepts a request but its response is lost. The guarantee is durable retryable processing with idempotent internal effects — not exactly-once delivery.

### 17.4 Availability and failure boundaries

Deploy on AKS with multiple API and worker replicas across failure domains. PostgreSQL needs its own HA, backup and recovery design; replicas do not compensate for an unavailable database. Schedulers may run on several instances using durable claims; correctness rests on idempotent jobs and guarded transitions, not on leader election alone.

DeNoise protects events **after** durable acceptance. Delivery before acceptance is a separate boundary: Azure webhook retries are finite and endpoints enter cooldown after failures, so prolonged Hub unavailability creates gaps the internal queue cannot recover. For critical sources, configure an independent notification path or source-status reconciliation. Reconciliation restores current knowledge; it does not recover every missed historical event.

The Hub runs on the same AKS platform it monitors. Failure of that cluster must not remove the only means of reporting the failure — see 13.7.

### 17.5 Replay

Three separate modes: preview interpretation without state change; retry previously failed events; rebuild historical projections without notifications. Replay records actor, selection, mapping version and outcome. Historical replay cannot reopen old episodes, reset live timers or send historical escalations.

---

## 18. Data volume, retention and privacy

### 18.1 Volume model (missing from both drafts)

The 3,000 events/minute figure in draft 0.2 was stated as a load test target with no baseline behind it. Its storage consequence, at an assumed 8 KB average payload:

| Sustained rate | Events/day | Raw bytes/day | Raw at 30-day retention |
|---|---|---|---|
| 50/min | 72,000 | ~0.6 GB | ~17 GB |
| 250/min | 360,000 | ~2.9 GB | ~86 GB |
| 3,000/min | 4,320,000 | ~35 GB | ~1 TB |

A single PostgreSQL instance holding a terabyte of raw payloads with a 30-day rolling delete is a different engineering problem from one holding 17 GB. The baseline in 3.3 decides which one this is.

Consequently: raw events are stored in a **time-partitioned table** with partition drop rather than row-level delete, from day one.

Heartbeat pings and canary events are excluded from this store. A one-minute heartbeat is ~43,000 calls per month per heartbeat; storing each as a retained raw event would make the ping traffic dominate the alert traffic. They update counters and a bounded ring of recent runs instead (§13.3.3), and are excluded from alert-volume metrics.

### 18.2 Retention

| Data | Initial retention |
|---|---|
| Raw payloads | 30 days |
| Normalised event history | 90 days |
| Episodes and operational actions | 12 months |
| Policy versions and audit | 12 months |
| Delivery-attempt detail | 30 days |

Open episodes retain the state needed for continued handling after raw-event expiry, and the UI states clearly when original payloads are gone. Replay is bounded by retained source data.

### 18.3 Privacy (missing from both drafts)

Alert payloads from Atlas and Azure Monitor can contain customer identifiers, hostnames, connection strings, query fragments and user email addresses. Before launch:

1. Classify each integration's payload for personal data, with the data protection function.
2. Define a redaction step at normalisation for fields known to carry personal data, with the raw copy access-restricted (19.1) and dropped at 30 days.
3. Confirm hosting region and that no payload content leaves the EU.
4. Confirm that notification bodies carry the minimum necessary and link to the authenticated detail page rather than embedding payload content.
5. Record a retention justification per data class.

Unresolved, this is a launch blocker rather than a technical detail.

---

## 19. Security and access

### 19.1 Requirements

SSO through the company identity provider. Roles: viewer, operator, integration administrator, platform administrator. Access controls enforced in both APIs and queries — not in the UI layer. Integration-specific credentials with defined scope and rotation. Source-supported signature validation. Request and payload size limits. Safe handling of untrusted payload content in the UI (alert summaries are attacker-influenced input and must be treated as such). Raw events and secrets restricted to authorised roles. Audit of configuration changes, assignment, acknowledgement, suppression, closure and replay.

### 19.2 Access scope

`access_scope` is server-assigned from the authenticated integration and maps to product boundaries — Marketplace (MPT), PRISM, CloudIQ, PyraCloud — resolved through Entra ID groups. A user sees an episode only if their group membership covers its scope. Groups never span scopes. This must be modelled explicitly before build; retrofitting a scope model onto an existing alert store is expensive.

---

## 20. Observability and targets

### 20.1 What to measure

Acceptance rate and failures; queue age and depth; parse failures by integration and mapping version; unassigned alerts and fallback routing; notification delay and delivery failures; overdue acknowledgement and follow-up; stale integrations and failed verification; automatic closures by evidence and reason; recurrence shortly after inferred resolution; scheduler lag and expired reservations.

Integration health is monitored independently of alert volume.

### 20.2 Proposed pilot targets

| Measure | Initial target |
|---|---|
| Durable acceptance | p95 < 500 ms at agreed load |
| Accepted event to visible state | p95 < 5 s |
| First notification attempt | p95 < 30 s |
| Queue interaction | p95 < 2 s at agreed dataset size |
| Timer execution lag | < 60 s in healthy operation |
| Availability objective | 99.9% initially; confirm against the coverage model in 7.3 |

Notification targets assume an available destination and exclude configured grouping delays. Load validation uses the measured peak from 3.3, not an invented figure, plus a short burst at 3× that peak. Backup restoration and failover are validated against agreed recovery objectives before production.

---

## 21. Rollout and exit criteria

1. Measure the baseline (3.3).
2. Close the build-versus-adopt gate (Section 6).
3. Validate lifecycle and identity against representative recorded source events.
4. Test the UI with operators performing real triage tasks.
5. Run ingestion and mapping in **shadow mode** with no Hub notifications, for at least two weeks, comparing Hub state against reality.
6. Pilot one team with explicit fallback coverage and existing routes still live.
7. Validate inactivity, replay, recovery, coverage-loss and delivery-failure scenarios.
8. Expand integrations progressively, one at a time, each with its coverage method configured.

No existing critical delivery route is removed until the corresponding Hub route **and** its fallback have been validated in production for an agreed period.

**Exit criteria (stop or reassess):**

- Shadow mode shows Hub state diverging from reality on more than an agreed share of episodes.
- The pilot team's acknowledgement behaviour does not improve over baseline after 8 weeks.
- Hub availability during pilot falls below the availability of the paths it replaces.
- Build effort exceeds the 6.2 estimate by more than 50% at the midpoint.

---

## 22. Acceptance criteria

These double as the assessment scenarios for adopted alternatives in Section 6.3.

| Scenario | Required result |
|---|---|
| Duplicate webhook delivery | No duplicate transition, escalation or occurrence, where source identity permits reliable detection |
| Two workers process one condition | One correct episode; atomic state change |
| Worker fails during processing | Work recovered; accepted event not lost |
| Worker fails after state commit | Notification job remains available |
| Delivery succeeds but response is lost | Retry controlled; possible duplicate recorded |
| Recovery arrives before opening | Delayed opening does not reactivate the recovered instance |
| Old recovery during a new episode | New episode stays active |
| No ownership rule matches | Alert reaches fallback triage, visibly unassigned |
| Two users take ownership | One consistent assignment; the other sees current state |
| Repeating source becomes quiet, coverage healthy | Inferred resolution after the validated deadline, evidence `heartbeat_and_inactivity` |
| Source becomes quiet, coverage never configured | Episode still closes on the Hub timer, evidence `inactivity_unverified`, visibly labelled |
| Azure `resolved` webhook is lost in transit | Hub backstop timer closes the episode; closure is not presented as source-confirmed |
| Operator changes an auto-resolve timeout | Preview shows how many open episodes it would affect before activation |
| Canary rule stops firing | Coverage alert within threshold; closure suspended for affected episodes, not closed as unverified |
| Registered heartbeat misses its schedule | One alert episode, owned and routed like any other; recovers with evidence `source` on the next ping |
| Heartbeat misses many intervals in a row | Still one episode, not one per missed interval |
| Cron heartbeat crosses a DST transition | No alarm for the hour that does not exist; no double alarm for the repeated hour |
| Heartbeat job calls `/fail` | Alert raised immediately without waiting for the grace period |
| Heartbeat token is rotated | Old token stops working; the heartbeat keeps its identity, history and state |
| Heartbeat is paused | Visibly paused with actor and reason; counted as a coverage gap, not as healthy |
| Maintenance window covers a heartbeat's scope | Heartbeat auto-pauses and resumes at window end without a false miss |
| DeNoise scheduler stops | External dead-man's switch reports it; missed heartbeats are not silently reported as healthy |
| Coverage alert flaps | One episode; resolves only after N consecutive successes |
| Ingestion or mapping unhealthy | Silence-based recovery postponed; coverage warning shown |
| Old alert reaches administrative expiry | Leaves active work with an unverified closure reason, not shown as recovery |
| New signal races with expiry | Stale timer cannot close the updated episode |
| Source resumes after expiry | New effective activity creates a fresh episode |
| Maintenance window ends with active condition | Current actionable notification emitted |
| Maintenance window crosses a DST transition | Window applies for the intended wall-clock period |
| New critical child joins a group | Remains visible and eligible to notify |
| Historical replay executes | No historical paging; no regression of live state |
| UI loses live connection | Freshness warning; no false appearance of current data |
| Manual close while source is firing | Last source condition and manual closure remain distinguishable |
| User lacks resource access | Data and actions unavailable through both UI and API |
| Webhook destination fails permanently | Fallback destination used; failure visible in Hub health; retries stop |
| Webhook destination fails transiently | Retried with backoff; same delivery id on every attempt |
| Raw payload retention expires on an open episode | Episode remains actionable; UI states the payload is gone |
| Hub or monitored cluster fails | Independent monitoring reports it over an independent path |
| **Out-of-hours critical alert** | Reaches a person, not only a channel — via the delegated rotation path in 7.3 |

Release readiness requires processing correctness **and** successful operator completion of the core UI workflows.

---

## 23. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| 1 | What does the 14-day baseline actually show? | Sylwester Grabowski | Before the gate |
| 2 | Does existing SoftwareOne Atlassian licensing already include JSM alerting at the required tier? | Sylwester Grabowski | Before the gate |
| 3 | What share of current alert volume is Azure-only and already addressable by alert processing rules? | Integration owner | Before the gate |
| 4 | Which team owns DeNoise in steady state, and who is on call for it? | CTO | Before the gate |
| 5 | Which external system is the out-of-hours escalation target until rotations exist? | Sylwester Grabowski | Before pilot |
| 6 | What is the cost and noise of an Azure canary rule per scope? | Integration owner | Before pilot |
| 7 | Does any launch source require email ingestion? | Integration owner | Before build |
| 8 | Data protection classification of Atlas and Azure payloads | DPO / security | Before pilot |
| 9 | Confirmed frontend standard for the UI | Platform Engineering | Before build |

---

## Appendix A — What changed from drafts 0.2 and 0.3

| # | Change | Reason |
|---|---|---|
| A1 | Added a blocking build-versus-adopt gate with cost, alternatives and named owner (§6) | 0.2 evaluated one alternative and justified building on "team maintainability" |
| A2 | Added a required baseline measurement task (§3.3) | All targets and the business case rested on unquantified assertions |
| A3 | Reduced 18 "goals" to 6 measurable outcomes (§4) | The old list was a feature inventory |
| A4 | Made auto-resolution a Hub-configured capability, on by default for every rule, with evidence — not closure itself — depending on source capability (§12.2) | 0.3 gated closure on heartbeats the launch sources don't emit, which would have meant auto-resolution never ran. Closure must not depend on producer good behaviour; that is the problem being solved |
| A5 | Separated coverage `unknown` (close, mark unverified) from coverage `degraded` (suspend), and added the `inactivity_unverified` evidence level (§12.4) | Conflating "not yet configured" with "actively broken" was the specific flaw |
| A5b | Made heartbeat a first-class capability with two mechanisms — self-service registered heartbeats via UI/API, and platform-provisioned managed canaries for centrally onboarded sources (§13.2–13.4) | 0.3 assumed a heartbeat that no launch source emits. If it does not exist, the Hub supplies it |
| A6 | Resolved the ownership contradiction: kept existing out-of-hours routes live and delegated rotations (§7.3) | Drafts forbade channel-as-owner while excluding the only mechanism that avoids it |
| A7 | Replaced the assumed Teams channel with generic, templated, signed outbound webhooks plus SMTP; Teams becomes a built-in template targeting Power Automate Workflows (§16.4) | O365 connectors were retired in May 2026; receiver churn belongs in configuration, not code |
| A8 | Added a volume and storage model with partitioned raw storage (§18.1) | Retention policy was stated without its storage consequence |
| A9 | Added privacy and data-classification requirements as a launch blocker (§18.3) | Neither draft mentioned personal data in payloads |
| A10 | Defined the severity enum and the term "actionable" (§2) | Both carried significant weight while undefined |
| A11 | Added access-scope model tied to product boundaries and Entra ID (§19.2) | `access_scope` appeared in the event model with no model behind it |
| A12 | Added rollout exit criteria and shadow-mode duration (§21) | Rollout had no stopping condition |
| A13 | Added acceptance cases: DST, coverage flapping, retention expiry, webhook failure, out-of-hours critical (§22) | Gaps in the original matrix |
| A14 | Added a queue-technology exit criterion (§17.2) | PostgreSQL-as-queue was presented without a limit |
