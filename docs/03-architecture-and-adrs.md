# 03 — Architecture and Decision Records

## 1. Component view

```
                        ┌──────────────────────────────────────────────────┐
  Azure Monitor ──┐     │  AlertHub.Ingest (public host)                   │
  Atlas ──────────┼──▶  │  POST /ingest/{integrationKey}   → raw_event +   │
  App webhooks ───┤     │  GET|POST /hb/{token}              job (1 txn)   │
  Cron jobs ──────┘     └──────────────┬───────────────────────────────────┘
                                       │ PostgreSQL
  ┌────────────────────────────────────▼──────────────────────────────────┐
  │  alert.raw_event (partitioned) │ ops.job │ alert.episode │ ops.outbox │
  └──────┬─────────────────────────┴────┬────┴───────┬───────┴─────┬──────┘
         │ FOR UPDATE SKIP LOCKED       │            │             │
  ┌──────▼──────────┐  ┌───────────────▼──┐  ┌──────▼─────┐  ┌────▼─────────────┐
  │ Processing      │  │ Scheduler        │  │ Api + SSE  │  │ Dispatcher       │
  │ normalise→dedup │  │ timers, coverage │  │ operator   │  │ outbox → webhook/│
  │ →lifecycle→     │  │ auto-resolve,    │  │ actions,   │  │ email, retries,  │
  │ route→outbox    │  │ escalation,      │  │ queries,   │  │ delivery_attempt │
  │                 │  │ retention        │  │ policies   │  │                  │
  └─────────────────┘  └──────────────────┘  └─────▲──────┘  └──────────────────┘
                                                   │ session cookie / PAT (local identity provider; OIDC later)
                                             React SPA (web/)
```

Three container images: `ingest`, `api`, `workers` (workers host runs processing, scheduler and dispatcher as separate `BackgroundService`s; scale by replica count and by role flag `--roles=processing,scheduler,dispatcher`).

## 2. Request paths

**Ingest:** authenticate integration → size/rate check → `INSERT raw_event` + `INSERT job(kind='normalise')` in one transaction → `202 Accepted {event_id}`. Nothing else. p95 target < 500 ms is trivially met if this path never touches mapping.

**Process:** claim job → load raw → select mapping version → normalise → compute `delivery_key` and `fingerprint` → in one transaction: insert `applied_event` (unique on delivery_key; conflict = duplicate, stop), upsert episode with version check, append `episode_event`, insert `audit_entry`, insert `outbox` rows, schedule/reschedule `job(kind='auto_resolve'|'ack_deadline'|...)` → commit → publish SSE change notification (best-effort, after commit).

**Scheduler:** claim due jobs → re-read episode with `FOR UPDATE` → recheck guards (version, latest signal, coverage, policy) → transition or reschedule → commit.

**Dispatch:** claim outbox row → check still relevant (episode state, suppression) → render → send → record `delivery_attempt` → mark sent/failed/coalesced → commit.

## 3. ADRs

### ADR-1: Modular monolith, three hosts, one database
**Status:** Accepted. **Context:** ~30-person department, 2–3 engineers on this. **Decision:** One solution, layered projects, three deployables sharing one PostgreSQL. **Alternatives:** Microservices per component (rejected: operational cost, distributed transactions across episode + outbox). **Consequences:** Deploy is simple; the DB is the single point that needs HA; schema changes are coordinated releases. Revisit if a second team needs to own a component independently.

### ADR-2: PostgreSQL as job queue and outbox
**Status:** Accepted. **Decision:** `ops.job` and `ops.outbox` tables claimed with `SELECT … FOR UPDATE SKIP LOCKED LIMIT n`, reservation token + expiry, attempt counter, exponential backoff with jitter. **Alternatives:** Azure Service Bus (rejected for v1: breaks the single-transaction guarantee between episode transition and notification job; adds a second stateful dependency). **Consequences:** Exactly-once *internal* effects; transactional coupling is free; throughput ceiling ~1,000 ev/min before re-evaluation (spec §17.2). Vacuum tuning on hot tables is mandatory (`fillfactor=70`, aggressive autovacuum settings).

### ADR-3: Minimal APIs, no controllers
**Status:** Accepted. Endpoints grouped per module (`MapEpisodeEndpoints()`, …). Consistent filter pipeline: auth → scope → `If-Match` → `Idempotency-Key` → handler.

### ADR-4: EF Core for writes and simple reads; Dapper for the claim path and queue read models
**Status:** Accepted. **Context:** The work-queue read model (filter/sort/paginate across episodes with joins to team, integration, group) and the claim path are the two hot spots. **Decision:** EF Core everywhere except those, which use hand-written SQL in `Infrastructure/ReadModels`. **Consequences:** Two data-access styles; contained in one folder; read-model SQL has integration tests.

### ADR-5: Declarative mapping via YAML DSL over JSONPath, interpreted, versioned
**Status:** Accepted. **Decision:** See 07. No scripting engine. Mapping versions immutable; each raw event records the version used. **Alternatives:** JMESPath (fine, less known in .NET), embedded C# scripting/Roslyn (rejected: security, spec §5.2 forbids user scripts), JSONata (rejected: library maturity in .NET). **Consequences:** New transformation *kinds* need a code release; that boundary is stated to stakeholders (spec §15.2).

### ADR-6: Database is the configuration source of truth; YAML is import/export
**Status:** Accepted. **Decision:** Integrations, mappings, routing rules, policies live in tables as immutable versions with `activated_at/deactivated_at`. UI edits create new versions. `GET …/export` returns YAML; `POST …/import` validates and creates a version (not activated until confirmed). **Consequences:** Config-as-code works through the API; there is exactly one activation path; preview/dry-run operates on unactivated versions.

### ADR-7: Notification adapters behind one `INotificationChannel` interface
**Status:** Accepted (revised). **Decision:** Exactly two channels in v1: `WebhookChannel` and `SmtpEmailChannel`. No vendor channels — Teams (Workflows), Slack, ticketing and automation platforms are `webhook` destinations with a body template. Each destination row stores `channel_type`, encrypted `url`/`headers`, `method`, `body_template` + `template_format`, `signing_secret`, `timeout`, `event_types[]`, `retry_policy`, and a required `fallback_destination_id`.

**Templating:** Fluid (Liquid for .NET) in strict, sandboxed mode — no custom filters that touch I/O, member access limited to the notification model, render timeout 200 ms, output size cap 256 KiB. Two formats: `json` (template is JSON; every `{{ }}` value is JSON-escaped by default so a payload field can never break the document) and `text`. Built-in templates shipped as read-only rows: `generic-json` (the full notification model), `teams-adaptive-card` (Adaptive Card 1.5 wrapped for a Workflows HTTP trigger), `slack-blocks`, `plain-text`. Administrators may clone and edit; templates are versioned like policies and must render a stored sample before activation.

**Request contract:** `POST` by default; headers `Content-Type`, `User-Agent: AlertHub/<ver>`, `X-AlertHub-Delivery-Id: <outbox_id>` (identical on every retry), `X-AlertHub-Event: <type>`, `X-AlertHub-Timestamp: <unix>`, `X-AlertHub-Signature: v1=<hex hmac-sha256(secret, timestamp + "." + body)>` when a signing secret is set. Timeout default 10 s. Response classification: 2xx success; 408/425/429/5xx/timeout/connection error retryable (backoff 30 s → 1 m → 5 m → 15 m → 1 h, max 10 attempts, honour `Retry-After`); any other 4xx permanent → fallback destination + `hub.delivery_failure`. First 4 KiB of the response body is stored on the delivery attempt for debugging. Redirects are not followed. Destination URLs must be `https` unless `allowInsecureDestinations` is set for dev; private-network targets are allowed (this runs inside AKS) but resolved IPs are logged.

**Alternatives:** vendor channels (rejected — receiver churn, the O365 connector retirement being the latest); Graph API (kept as a possible future channel behind the same interface). **Consequences:** Teams provisioning is a Workflow the channel owner creates, whose URL is pasted into a destination; the Hub never holds Graph permissions; every receiver quirk is a template edit.

### ADR-8: Time
**Status:** Accepted. `timestamptz` everywhere; API uses RFC 3339 UTC. IANA timezone strings only on `heartbeat.schedule_tz` and `maintenance_window.tz`. `TimeProvider` injected; tests use `FakeTimeProvider`. Cron via Cronos with `TimeZoneInfo` — DST tests are mandatory.

### ADR-9: Observability
**Status:** Accepted. OpenTelemetry SDK; OTLP exporter configured via env. Required metrics (names are the contract):
`alerthub_ingest_accepted_total{integration}`, `alerthub_ingest_rejected_total{reason}`, `alerthub_job_queue_depth{kind}`, `alerthub_job_oldest_age_seconds{kind}`, `alerthub_episode_transitions_total{from,to,reason}`, `alerthub_episode_open{severity,team}`, `alerthub_closure_total{reason,evidence}`, `alerthub_notification_latency_seconds`, `alerthub_delivery_attempts_total{channel,outcome}`, `alerthub_coverage_state{integration,state}`, `alerthub_heartbeat_state{state}`, `alerthub_scheduler_lag_seconds`, `alerthub_mapping_failures_total{integration,mapping_version}`, `alerthub_unassigned_open`. Serilog enrichers: `TraceId`, `IntegrationId`, `EpisodeId`, `ActorId`.

### ADR-10: Migrations as pre-upgrade Job
**Status:** Accepted. `AlertHub.Migrator` console project; Helm `pre-upgrade` hook; app containers never run migrations. Expand/contract for breaking changes.

### ADR-11: Secrets
**Status:** Accepted. Platform secrets (DB connection, cookie data-protection keys, SMTP, later OIDC client secret); destination URLs, headers and signing secrets are per-row data encrypted with a data-protection key from Key Vault via Azure Key Vault + CSI driver → env/file. Integration ingest tokens and heartbeat tokens: 32 random bytes, base64url, stored as Argon2id hash with per-row salt; lookup by a non-secret `key_id` prefix in the URL/header so verification is a single-row hash compare. Destination endpoint URLs (Workflows) encrypted at rest with a data-protection key from Key Vault.

### ADR-14: Local identity provider first, OIDC later
**Status:** Accepted. **Context:** Entra ID app registration and group mapping (C2) are not available at project start; the owner wants username/password for v1. **Decision:** Authentication sits behind `IIdentityProvider` with two implementations planned: `LocalPasswordProvider` (v1) and `OidcProvider` (later). Authorization (roles, scopes, team membership) is **always local** — stored on `cfg.user` regardless of provider — so switching providers changes how a user proves identity, not what they may do. Users carry `auth_provider ∈ {local, oidc}` and `external_id`; the login page shows an SSO button when an OIDC provider is configured. **Local provider rules:** Argon2id (m=64 MiB, t=3, p=1); min 12 chars checked against a bundled breached-password list; lockout 10 failures / 15 min per user and per IP; forced change on first login; admin-initiated reset (temporary password, one-time display) plus optional self-service reset by email when SMTP is configured; server-side sessions (revocable) with cookie `HttpOnly Secure SameSite=Lax`, sliding 8 h / absolute 24 h; CSRF token on mutations. **Personal access tokens** (scoped to the user's permissions, optional expiry, hashed, prefix lookup as ADR-11) for API clients and config-as-code. **Consequences:** Alert Hub becomes a credential store — offboarding is manual until OIDC lands; TOTP for `platform_admin` is recommended before pilot and is a small addition on this design. **Migration to OIDC:** add provider, match existing users on email, set `auth_provider=oidc`, disable password; permissions untouched.

### ADR-12: SSE for live updates
**Status:** Accepted. `GET /api/v1/events/stream?scope=…` with `text/event-stream`; server publishes `episode.changed`, `coverage.changed`, `heartbeat.changed`, `hub.health` with ids; client reconnects with `Last-Event-ID`; server keeps a 5-minute ring buffer per scope; on gap, client refetches. **Alternatives:** SignalR (rejected: bidirectional not needed; scale-out needs a backplane). Polling fallback at 30 s if SSE unavailable.

### ADR-13: Frontend architecture
**Status:** Accepted pending C1. SPA; routes in 08; TanStack Query for all server state with `staleTime` tuned per screen; SSE invalidates queries by key; URL is the source of truth for filters (shareable links). No global client store beyond auth and SSE connection state.

## 4. Deployment shape (Helm values that must exist)

`ingest.replicas`, `api.replicas`, `workers.replicas`, `workers.roles`, `postgres.connectionSecret`, `auth.local.*` (bootstrap admin password, password policy, lockout), `auth.oidc.*` (optional, later), `smtp.*`, `externalDeadmanUrl`, `retention.*`, `limits.payloadBytes`, `limits.ingestRps`, `otel.endpoint`, `publicBaseUrl` (for links in notifications), `ingestPublicBaseUrl` (for heartbeat URLs shown in UI).

Pod disruption budgets for all three; anti-affinity across zones; `workers` uses a startup probe that waits for DB.
