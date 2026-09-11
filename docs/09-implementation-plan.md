# 09 — Implementation Plan

Vertical slices, each shippable and testable on its own. Milestone numbers are also branch prefixes (`m3/…`). Acceptance scenario names refer to spec §22. Owner inputs refer to 01 §C.

Effort is indicative for one agent-assisted engineer; treat as ordering, not commitment.

| M | Slice | Delivers | Acceptance scenarios | Needs from owner |
|---|---|---|---|---|
| **0** | Skeleton | Solution layout (02 §3), CI (build, test, lint, OpenAPI drift), Docker Compose (Postgres, Mailpit, mock OIDC), Helm chart skeleton, first migration with **partitioned** `raw_event`, `ops.job`, `ops.outbox`, health endpoints, OpenTelemetry wiring, `TimeProvider` | — | none |
| **1** | Durable ingest + queue | `/ingest/{key}` with token auth, size/rate limits, atomic raw+job insert, 202 contract; job claim/complete/reap with reservation tokens; failure queue | *Worker fails during processing*, *Worker fails after state commit* (job part) | none |
| **2** | Normalise + identity + episode | Mapping DSL interpreter (07 §1, all rule kinds), validation, versioning; delivery key (04 §3); fingerprint (04 §4); ordering (04 §5); episode create/update/resolve; timeline; audit; **generic webhook mapping** | *Duplicate webhook delivery*, *Two workers process one condition*, *Recovery arrives before opening*, *Old recovery during a new episode*, *Historical replay executes* (no-transition part) | none |
| **3** | Ownership + routing + outbox | Teams, scopes, users (seeded), routing predicate engine (07 §5), fallback triage, escalation policies, outbox rows, dispatcher with `INotificationChannel`, **`WebhookChannel` with the `generic-json` body, signing and retry classification**, **SMTP channel** to Mailpit, relevance check, coalescing | *No ownership rule matches*, *Worker fails after state commit* (outbox part), *Delivery succeeds but response is lost* | C4 (SMTP details; Mailpit until then) |
| **4** | Application API + auth + queue UI | Local auth (login, server-side sessions, lockout, forced change, admin user management, PATs, CSRF), RBAC matrix, episode endpoints (list/detail/timeline/actions/bulk), `If-Match`, `Idempotency-Key`, SSE; React: shell, auth, queue, detail overview + timeline, FreshnessBar | *Two users take ownership*, *UI loses its live connection*, *User lacks resource access*, *Manual close while source is firing*; auth: lockout, forced change, PAT scope, session revoke | none — C1 resolved by 08 §0; C2 deferred to 11b |
| **5** | Auto-resolve + expiry + coverage state | Lifecycle policies, `auto_resolve`/`verify_state`/`admin_expiry`/`stale_review` jobs with guards (04 §5.3), evidence model, `unknown` vs `degraded` handling, coverage state machine (04 §9), coverage episodes, **external dead-man ping job** | *Repeating source becomes quiet (coverage healthy)*, *Source quiet, coverage never configured*, *Azure `resolved` lost in transit*, *Old alert reaches administrative expiry*, *New signal races with expiry*, *Source resumes after expiry*, *Ingestion or mapping unhealthy*, *Canary rule stops firing*, *Coverage alert flaps*, *Operator changes an auto-resolve timeout* (impact preview API) | C9 (baseline, for defaults) |
| **6** | Heartbeats | `/hb` ping API, `hb` schema, state machine (04 §10), cron with tz (DST tests), miss episodes, pause/resume, token rotate, maintenance auto-pause, binding to integration coverage; React: heartbeat list/create/detail with one-time URL dialog and snippets; YAML import/export | *Registered heartbeat misses its schedule*, *misses many intervals*, *Cron heartbeat crosses DST*, *`/fail`*, *Token rotated*, *Paused*, *Maintenance covers heartbeat*, *Alert Hub scheduler stops* | none |
| **7** | Webhook templates + destinations | Fluid template engine (json/text, sandboxed), built-in templates (`generic-json`, `teams-adaptive-card`, `slack-blocks`, `plain-text`), template versioning + render preview + activate, destination CRUD with event-type subscriptions, test send, deliveries view, mandatory fallback, permanent-failure → fallback + `hub.delivery_failure`; React: destinations and templates screens | *Webhook destination fails permanently*, *fails transiently* | a receiver URL for the pilot (any Workflow, Slack app or request-bin) — not a design dependency |
| **8** | Azure Monitor + Atlas integrations | Reference mappings (07 §2–3) as seeded versions; canary rule mapping (`event_type: heartbeat`); Atlas HMAC verification (algorithm configurable); Atlas Admin API state query adapter (`queryable_state`), api probe; integration wizard UI; failures/replay UI | *Explicit-recovery-style Azure flows*, *Ingestion or mapping unhealthy* (mapping failure rate) | **C5** (real payload captures), Atlas API credentials for a test project |
| **9** | Grouping + suppression + team overview | Grouping rules and windows, group severity, suppression/maintenance with tz, post-suppression summary, team overview screen, history screen, saved filters | *Maintenance window ends with active condition*, *Maintenance crosses DST*, *New critical child joins a group* | none |
| **10** | Policies UI + config-as-code | YAML editors, diff, impact preview, activate/rollback for all policy kinds; export/import; audit screen | — | none |
| **11** | Retention + hub health + hardening | Retention jobs (05 §7), partition management, hub health screen, failure queue UI, rate-limit tuning, payload-retention UI flag, load test (k6) at baseline ×1 sustained and ×3 burst, backup/restore drill script | *Raw payload retention expires on an open episode*, *Hub or monitored cluster fails* (via dead-man + runbook) | C9 (measured peak), C10 (Helm/GitOps conventions) |
| **11b** | OIDC provider (Entra ID) — *when C2 is available; any time after M4* | `OidcProvider`, SSO button, user matching on email, password disable; TOTP for platform_admin if not done earlier | login via SSO; *User lacks resource access* re-run | **C2** |
| **12** | Shadow mode + pilot readiness | "Shadow" flag per integration (ingest + process, no outbox); divergence report (Hub state vs source state via API); operator UI usability session script; pilot runbook; decommission checklist per legacy route | *Out-of-hours critical alert* (with C7 target) | **C7** (out-of-hours target), pilot team |

## Ordering rationale

- M0–M3 have zero owner dependencies and establish every invariant that is expensive to retrofit (partitioning, idempotency ledger, outbox, audit-in-transaction).
- Auth and UI (M4) come before auto-resolve (M5) so every later slice is demonstrable in the product, not only in tests. Local auth keeps M4 free of external dependencies; OIDC is additive (11b).
- Heartbeats (M6) precede the real integrations (M8) because the coverage model must exist before Azure/Atlas canaries can bind to it.
- The webhook channel itself ships in M3 so every later milestone can be demonstrated against a request-bin; M7 adds templating and destination management on top.

## Parallelism

Frontend work for a milestone can start as soon as that milestone's OpenAPI is merged (M4 onward). Mapping DSL (M2) and routing engine (M3) are independent modules and can be built in parallel.

## Explicit non-work

Do not build: on-call rotations, incident objects, metric evaluation, remediation hooks, email ingestion (unless C8 says yes), Polish localisation, multi-tenant boundaries beyond `access_scope`.

## Exit checkpoints (from spec §21)

- After M5: run a two-week shadow on the generic webhook producers; report divergence.
- After M8: shadow on Azure + Atlas; **gate** pilot on divergence below the agreed threshold and on all §22 scenarios green.
- Pilot: one team, legacy routes live, 8-week measurement against G1–G6.
