# 01 — Requirements Completeness Review

**Purpose:** What a coding agent would need that the specification (v0.4) does not say, and how each gap is closed in this pack.

**Method:** Each requirement in the spec was tested against the question *"could an agent implement this without inventing something?"* Every place the answer was no is listed here.

Legend — **D** = decided in this pack (overridable by the owner), **O** = open, needs the owner, **N** = noted, agent may choose.

---

## A. Gaps that would block implementation

| # | Gap in spec v0.4 | Consequence if left open | Resolution | Where |
|---|---|---|---|---|
| A1 | No API contract for ingestion or the application API — only the heartbeat API has endpoints | Agent invents routes; UI and backend drift | **D** — full endpoint list, auth, error format, pagination | 06 |
| A2 | No data schema: entities named but no keys, uniqueness, indexes, partitioning | Dedup and idempotency guarantees cannot be enforced at the DB layer | **D** — DDL sketch with the invariants that must be constraints, not code | 05 |
| A3 | State transitions described in prose, not enumerated | Guards get missed; late-event rules implemented inconsistently | **D** — transition tables with guards and side effects | 04 |
| A4 | Fingerprint canonicalisation undefined (ordering, case, nulls, hash) | Same condition yields different fingerprints across workers | **D** — canonical JSON + SHA-256, explicit rules | 04 §4 |
| A5 | Mapping DSL has a vocabulary but no syntax | The single largest area an agent would improvise | **D** — YAML DSL over JSONPath, with reference mappings for Azure CAS and Atlas | 07 |
| A6 | Routing-rule and grouping-key expression language undefined | Same as A5 | **D** — same predicate grammar reused for routing, grouping, suppression scope | 07 §5 |
| A7 | Escalation policy has no structure | "Static escalation policies" is unimplementable as stated | **D** — ordered steps with delay, targets, repeat count | 04 §7 |
| A8 | Notification payloads and channels unspecified | Agent would build vendor channels that churn | **D** — generic outbound webhook (templated, signed, retry-classified) + SMTP; Teams is a built-in template, not a channel | 03 ADR-7, 06 §7 |
| A9 | Ingestion authentication unspecified beyond "credentials" | Security design deferred to the agent | **D** — per-integration bearer token in header; Atlas HMAC verification; optional IP allow-list | 06 §2 |
| A10 | RBAC listed as four roles, no permission matrix | Permission checks implemented ad hoc | **D** — action × role matrix | 04 §8 |
| A11 | Real-time UI updates mechanism unspecified | SignalR vs SSE vs polling changes the backend | **D** — SSE, one stream per client, event types listed | 06 §8, 08 |
| A12 | Configuration source of truth unclear (YAML shown, UI described) | Two config paths that diverge | **D** — DB is truth; YAML is import/export format; policy versions immutable | 03 ADR-6 |
| A13 | No .NET / React versions, libraries, or project layout | Agent picks; may conflict with company standards | **D** — .NET 10 LTS backend; React 19 frontend with an explicit design direction (08 §0) | 02, 03, 08 |
| A14 | No error model for APIs | Inconsistent error responses | **D** — RFC 9457 Problem Details | 06 §1 |
| A15 | Operator action idempotency and optimistic concurrency mechanics not specified | Double acknowledgements on retry; lost updates | **D** — `If-Match` with episode version; `Idempotency-Key` on mutating endpoints | 06 §1 |

## B. Gaps that would cause wrong behaviour later

| # | Gap | Resolution | Where |
|---|---|---|---|
| B1 | Time handling: UTC vs local, testable clock | **D** — UTC everywhere in storage and API; `TimeProvider` injected; IANA tz only in schedules and maintenance windows | 03 ADR-8 |
| B2 | Retention jobs described as policy, not as work | **D** — partition-drop job for raw events; row-delete jobs for others; schedule and safety | 05 §6 |
| B3 | Audit entry has no schema | **D** — actor, action, target, before/after JSON, correlation id | 05 §4 |
| B4 | Bulk action limits, rate limits, payload limits have no values | **D** — initial values, all configurable | 06 §1 |
| B5 | Search semantics ("searchable") undefined | **D** — Postgres full-text on summary/resource/rule; structured filters for everything else. No external search engine in v1 | 05 §5 |
| B6 | Email delivery mechanism unspecified | **D** — SMTP relay via configuration; Graph send-mail as a pluggable alternative behind the same interface | 03 ADR-7 |
| B7 | Test fixtures: real Azure/Atlas payloads are needed and cannot be invented | **O** — owner must supply captured samples; Microsoft's published Common Alert Schema samples may be used as a starting point and must be marked as such | 10 §3 |
| B8 | Health/readiness/liveness endpoints for AKS not mentioned | **D** — `/healthz/live`, `/healthz/ready`, `/healthz/startup`; readiness includes DB and outbox lag | 06 §9 |
| B9 | Observability stack unspecified | **D** — OpenTelemetry traces/metrics/logs; Serilog structured logging; metrics named in 03 | 03 ADR-9 |
| B10 | Migration strategy for schema changes on AKS | **D** — EF Core migrations run as a pre-deploy Job, never on app start | 03 ADR-10 |
| B11 | Secret handling (integration tokens, heartbeat tokens, SMTP creds) | **D** — Key Vault via CSI driver for platform secrets; integration/heartbeat tokens stored hashed (Argon2id) — plaintext shown once | 03 ADR-11 |
| B12 | Heartbeat ping endpoint on the public ingestion host vs application host | **D** — public ingestion host; separate path prefix; separate rate-limit bucket | 06 §3 |
| B13 | What "material severity increase" means for re-notification | **D** — any increase that crosses into `high` or `critical`, or any increase when the episode is `acknowledged` for more than the follow-up window | 04 §6 |
| B14 | Group window semantics (sliding vs fixed) | **D** — fixed window from first member; new groups after expiry | 04 §7 |

## C. Decisions the owner must still make (not resolvable by an agent)

| # | Question | Why it blocks | Default the pack assumes until answered |
|---|---|---|---|
| C1 | ~~Company frontend standard~~ **Resolved by owner: modern UI built to the design direction in 08 §0; no company design-system dependency** | — | — |
| C2 | Entra ID tenant/app registration, group → team mapping | **Deferred by owner:** v1 uses local username/password (ADR-14); OIDC is milestone 11b | Local identity provider; permissions stored locally so the switch is additive |
| C3 | ~~Teams Workflows provisioning~~ **Resolved by owner: no Teams channel; delivery is via generic webhooks. Only a receiver URL is needed for the pilot** | — | — |
| C4 | SMTP relay details or Graph app permissions | Email delivery | SMTP with config placeholders |
| C5 | Captured real payload samples: Azure CAS (metric, log, activity log, resource health), Atlas (at least 5 alert types), each in-house webhook producer | Mapping correctness is unverifiable without them | Microsoft doc samples, marked provisional |
| C6 | Product → scope → Entra group table for MPT, PRISM, CloudIQ, PyraCloud | Access control | Placeholder table |
| C7 | Out-of-hours escalation target system (§7.3 of spec) | Escalation policies need a real terminal target | Email to a configured address; **must** be replaced before pilot |
| C8 | Whether Atlas integration needs email ingestion at all | Scope | Not built |
| C9 | The measured baseline (spec §3.3) — event rates and payload sizes | Sizing, partition granularity, load-test targets | Daily partitions; load test at 500 ev/min sustained, 3,000 burst |
| C10 | Helm/GitOps conventions of the existing AKS platform | Deployment manifests | Plain Helm chart in-repo; align with existing GitOps onboarding model |

## D. Requirements verified as complete enough to implement

Lifecycle dimensions and closure reasons (§11); source profiles and auto-resolve semantics (§12); heartbeat object model and ping API (§13.3); coverage states (§13.5); UI screens and queue views (§14); ingestion ordering contract (§15.1); grouping/suppression rules (§16.3); PostgreSQL queue and outbox pattern (§17.2–17.3); retention values (§18.2); acceptance criteria (§22).

## E. Requirements deliberately not in v1 and confirmed absent from this pack

Metric/log evaluation; RCA; remediation; full incident management; on-call rotations (delegated per §7.3); telephone paging; bidirectional source sync; multi-tenant SaaS; email ingestion (unless C8 says otherwise); Polish UI localisation.

---

## Summary

- **15 blocking gaps**, all closed with decisions in this pack.
- **14 behaviour gaps**, 13 closed, 1 (fixtures) needs owner input.
- **7 owner decisions** outstanding (C1, C3 resolved; C2 deferred). **C5** should be answered before the integration milestone; everything else can be deferred to the milestone that needs it (see 09).

An agent can start Milestones 0–4 in the implementation plan today with no further input.
