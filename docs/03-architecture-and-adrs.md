# 03 — Architecture and Decision Records

## 1. Component view

```
                        ┌──────────────────────────────────────────────────┐
  Azure Monitor ──┐     │  DeNoise.Ingest (public host)                   │
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

**Request contract:** `POST` by default; headers `Content-Type`, `User-Agent: DeNoise/<ver>`, `X-DeNoise-Delivery-Id: <outbox_id>` (identical on every retry), `X-DeNoise-Event: <type>`, `X-DeNoise-Timestamp: <unix>`, `X-DeNoise-Signature: v1=<hex hmac-sha256(secret, timestamp + "." + body)>` when a signing secret is set. Timeout default 10 s. Response classification: 2xx success; 408/425/429/5xx/timeout/connection error retryable (backoff 30 s → 1 m → 5 m → 15 m → 1 h, max 10 attempts, honour `Retry-After`); any other 4xx permanent → fallback destination + `hub.delivery_failure`. First 4 KiB of the response body is stored on the delivery attempt for debugging. Redirects are not followed. Destination URLs must be `https` unless `allowInsecureDestinations` is set for dev; private-network targets are allowed (this runs inside AKS) but resolved IPs are logged.

**Alternatives:** vendor channels (rejected — receiver churn, the O365 connector retirement being the latest); Graph API (kept as a possible future channel behind the same interface). **Consequences:** Teams provisioning is a Workflow the channel owner creates, whose URL is pasted into a destination; the Hub never holds Graph permissions; every receiver quirk is a template edit.

### ADR-8: Time
**Status:** Accepted. `timestamptz` everywhere; API uses RFC 3339 UTC. IANA timezone strings only on `heartbeat.schedule_tz` and `maintenance_window.tz`. `TimeProvider` injected; tests use `FakeTimeProvider`. Cron via Cronos with `TimeZoneInfo` — DST tests are mandatory.

### ADR-9: Observability
**Status:** Accepted. OpenTelemetry SDK; OTLP exporter configured via env. Required metrics (names are the contract):
`denoise_ingest_accepted_total{integration}`, `denoise_ingest_rejected_total{reason}`, `denoise_job_queue_depth{kind}`, `denoise_job_oldest_age_seconds{kind}`, `denoise_episode_transitions_total{from,to,reason}`, `denoise_episode_open{severity,team}`, `denoise_closure_total{reason,evidence}`, `denoise_notification_latency_seconds`, `denoise_delivery_attempts_total{channel,outcome}`, `denoise_coverage_state{integration,state}`, `denoise_heartbeat_state{state}`, `denoise_scheduler_lag_seconds`, `denoise_mapping_failures_total{integration,mapping_version}`, `denoise_unassigned_open`. Serilog enrichers: `TraceId`, `IntegrationId`, `EpisodeId`, `ActorId`.

### ADR-10: Migrations as a one-shot run before the rollout
**Status:** Accepted; revised 2026-10-22 (was: a Helm `pre-upgrade` hook). **Decision:** `DeNoise.Migrator` is a console project with its own image, `denoise-migrator`. It applies pending EF migrations, ensures the `raw_event` partitions and exits 0, or 1 on failure. It needs only `ConnectionStrings__DeNoise` (§4). App containers never run migrations. Running it is the deploy tooling's job, not this repository's. Before every rollout the tooling runs the migrator image at the release tag, waits for it to succeed, and only then rolls api, ingest and workers to the same tag. A failed migration stops the release while the previous revision keeps serving. Today the tooling is the Container Apps Job `<prefix>-caj-migrate` in the separate release repository, which its deploy workflow starts before applying the apps. Locally it is the `migrator` service in `docker-compose.yml`, which the three hosts wait for (`service_completed_successfully`), or `dotnet run --project src/DeNoise.Migrator`. **Expand/contract** for breaking changes: the previous revision runs against the migrated schema until the rollout finishes, and again after a rollback, so every migration must work with the previous release's code. Add first, switch the code in a release, and remove in a later release. **Alternatives:** migrating on app start (rejected: replicas race each other, and a slow migration holds every host out of readiness); a Helm hook (the first version of this ADR, dropped together with the chart because no release uses Helm). **Consequences:** a release is two steps in a fixed order, and the deploy tooling must keep that order. Migrations stay forward-only (AGENTS.md rule 13): rolling back means running the previous image tag against the newer schema, never reverting the schema.

### ADR-11: Secrets
**Status:** Accepted. Platform secrets (DB connection, cookie data-protection keys, SMTP, later OIDC client secret); destination URLs, headers and signing secrets are per-row data encrypted with a data-protection key from Key Vault, which the platform passes to the hosts as secret environment variables (§4, [ADR-18](adr/ADR-018-data-protection-key-ring.md)). Integration ingest tokens and heartbeat tokens: 32 random bytes, base64url, stored as Argon2id hash with per-row salt; lookup by a non-secret `key_id` prefix in the URL/header so verification is a single-row hash compare. Destination endpoint URLs (Workflows) encrypted at rest with a data-protection key from Key Vault.

### ADR-14: Local identity provider first, OIDC later
**Status:** Accepted; **revised by [ADR-15](adr/ADR-015-entra-app-roles-and-local-enforcement.md)** on the source of roles. **Context:** Entra ID app registration and group mapping (C2) are not available at project start; the owner wants username/password for v1. **Decision:** Authentication sits behind `IIdentityProvider` with two implementations planned: `LocalPasswordProvider` (v1) and `OidcProvider` (later). Authorization (roles, scopes, team membership) is **always enforced locally** — read from `cfg.user` regardless of provider — so switching providers changes how a user proves identity, not how a request is authorised. *(ADR-15: "locally enforced" is not "locally decided". Once OIDC is in place, roles come from Entra **app roles** and `access_scope` from Entra **groups**, projected onto `cfg.user` at sign-in; team membership, service accounts and the break-glass administrator stay local-only. Enforcement still reads the local row, which is what keeps PATs, sessions, CSRF and SSE unchanged.)* Users carry `auth_provider ∈ {local, oidc}` and `external_id`; the login page shows an SSO button when an OIDC provider is configured. **Local provider rules:** Argon2id (m=64 MiB, t=3, p=1); min 12 chars checked against a bundled breached-password list; lockout 10 failures / 15 min per user and per IP; forced change on first login; admin-initiated reset (temporary password, one-time display) plus optional self-service reset by email when SMTP is configured; server-side sessions (revocable) with cookie `HttpOnly Secure SameSite=Lax`, sliding 8 h / absolute 24 h; CSRF token on mutations. **Personal access tokens** (scoped to the user's permissions, optional expiry, hashed, prefix lookup as ADR-11) for API clients and config-as-code. **Consequences:** DeNoise becomes a credential store — offboarding is manual until OIDC lands; TOTP for `platform_admin` is recommended before pilot and is a small addition on this design. **Migration to OIDC:** add provider, match existing users on email, set `auth_provider=oidc`, disable password; permissions untouched for those accounts, and from then on projected from Entra per ADR-15. Keep one `local_only` break-glass `platform_admin`: DeNoise is the system that reports that Entra is unreachable, so it must not require Entra to sign in while diagnosing that.

### ADR-12: SSE for live updates
**Status:** Accepted. `GET /api/v1/events/stream?scope=…` with `text/event-stream`; server publishes `episode.changed`, `coverage.changed`, `heartbeat.changed`, `hub.health` with ids; client reconnects with `Last-Event-ID`; server keeps a 5-minute ring buffer per scope; on gap, client refetches. **Alternatives:** SignalR (rejected: bidirectional not needed; scale-out needs a backplane). Polling fallback at 30 s if SSE unavailable.

### ADR-13: Frontend architecture
**Status:** Accepted pending C1. SPA; routes in 08; TanStack Query for all server state with `staleTime` tuned per screen; SSE invalidates queries by key; URL is the source of truth for filters (shareable links). No global client store beyond auth and SSE connection state.

## 4. Deployment shape (environment-variable contract)

This repository builds and publishes container images only. `build/Dockerfile` builds four images, one per host, and `.github/workflows/release-images.yml` publishes them as `ghcr.io/<owner>/denoise-{api,ingest,workers,migrator}` with one shared tag. There are no deployment manifests here. Whatever runs the images supplies the configuration below as environment variables. Today that is the separate release repository, which runs them on Azure Container Apps (`container_app.tf`: `local.env_plain`, `local.env_secret_app`). Locally it is `docker compose --profile app up --build`.

ASP.NET Core maps `__` to `:`, so `Auth__Local__LockoutWindow` is `Auth:Local:LockoutWindow` in `appsettings.json`. An absent variable means the default from the options class. Prefer leaving a variable out over setting it empty: an empty string is a value, and it disables the `DeNoise__*` URL aliases below. Durations use `hh:mm:ss` and booleans use `true`/`false`.

### 4.1 Runtime shape

| Image | Runs as | Ports, health | Notes |
|---|---|---|---|
| `denoise-api` | long-running, ≥ 1 replica (several need a shared key ring, ADR-18) | HTTP `8080`; `/healthz/startup`, `/healthz/ready`, `/healthz/live` | Application API, SSE and the web UI (see below). Public ingress on the host of `DeNoise__PublicBaseUrl`. |
| `denoise-ingest` | long-running, ≥ 1 replica | HTTP `8080`; same three probes | Alert ingestion and heartbeat pings. Public ingress on the host of `DeNoise__IngestPublicBaseUrl`. |
| `denoise-workers` | long-running, ≥ 1 replica, no ingress | HTTP `8080` for the three probes only | Roles from the `--roles=processing,scheduler,dispatcher` argument or `Workers__Roles` (the argument wins; default: all three). At least one replica must run the scheduler role, which drives timers, retention and the dead-man ping. |
| `denoise-migrator` | one-shot, before every rollout (ADR-10) | none | Exit 0 = migrated, 1 = failed. Extra commands: `seed-dev` (development data) and `generate-dataprotection-key` (ADR-18, no database). |

- The image sets `ASPNETCORE_URLS=http://+:8080` and `DOTNET_gcServer=0`. Override them only to change the port or the GC mode.
- The images run as the non-root user `denoise`. The root filesystem can be read-only; only `/tmp` must be writable.
- `api` and `ingest` trust `X-Forwarded-For` / `X-Forwarded-Proto` from any proxy. Put them behind one that sets these headers, and never expose them directly. Source IPs for rate limits and audit come from these headers.
- `/healthz/startup` and `/healthz/ready` check the database; `/healthz/live` checks only the process. Probe start-up rather than liveness while a host waits for PostgreSQL, and allow it a few minutes.
- Run api and ingest with two or more replicas spread across zones where the platform allows.

### 4.2 Variables

*Hosts* lists the hosts in which a variable has an effect. Passing the whole set to every host is harmless; ops-alertsstore-release does that for the plain variables. *Secret* variables come from a secret store (Key Vault references in Container Apps) and must never appear in plain configuration or logs.

**Required in production**

| Variable | Meaning | Default | Hosts | Secret |
|---|---|---|---|---|
| `ConnectionStrings__DeNoise` | Npgsql connection string of the single PostgreSQL database. | none: host start-up fails | api, ingest, workers, migrator | secret |
| `Auth__Local__BootstrapPassword` | Password of the `admin` user created on the first Api start when no user exists, with `must_change_password=true`. Ignored once a user exists. Never logged. | unset: the Api generates one and prints it once to stdout | api | secret |
| `DataProtection__Keys__<n>` | Key ring documents from `DeNoise.Migrator generate-dataprotection-key`, kept in Key Vault; `__0`, then `__1`, … during a rotation. The ring is read-only (ADR-18). Must be identical in api, ingest and workers. | unset: in-memory ring per process (tests and single-run development only) | api, ingest, workers | secret |
| `DeNoise__PublicBaseUrl` | Base URL of the UI (the api host), used for links in notifications. Alias of `Notifications__PublicBaseUrl`, which wins when set. | `http://localhost:5173` | api, workers | plain |
| `DeNoise__IngestPublicBaseUrl` | Base URL of the ingest host, used to build the heartbeat ping URLs shown in the UI. Alias of `Heartbeats__IngestPublicBaseUrl`, which wins when set. | `http://localhost:8081` | api, workers | plain |
| `DeNoise__ExternalDeadmanUrl` | External dead-man's switch (spec §13.7), pinged every minute by the scheduler after a database check. Unset disables the ping, logs a launch-blocker warning, and the hub health screen shows it unconfigured. | unset | workers, api (health screen) | plain |

**Data Protection alternatives** (instead of `DataProtection__Keys__<n>`, ADR-18)

| Variable | Meaning | Default | Hosts | Secret |
|---|---|---|---|---|
| `DataProtection__KeysPath` | Directory of key files shared by all hosts, for example a writable shared volume. Setting it together with `DataProtection__Keys__<n>` fails start-up. | unset | api, ingest, workers | plain |
| `DataProtection__ReadOnly` | With `KeysPath`: never create keys, for example for a mounted secret directory. | `false` | api, ingest, workers | plain |

**Observability and health**

| Variable | Meaning | Default | Hosts | Secret |
|---|---|---|---|---|
| `Otel__Endpoint` | OTLP endpoint for traces and metrics (ADR-9). Falls back to `OTEL_EXPORTER_OTLP_ENDPOINT`; neither set disables the exporter. Logs always go to stdout as JSON. | unset | api, ingest, workers | plain |
| `Health__OutboxLagThreshold` | The Api reports not-ready when the oldest pending outbox row is older than this (06 §8). | `00:05:00` | api | plain |

**Limits** (06 §2, §6)

| Variable | Meaning | Default | Hosts | Secret |
|---|---|---|---|---|
| `Limits__PayloadBytes` | Maximum ingest request body; larger bodies get 413. | `262144` (256 KiB) | ingest | plain |
| `Limits__IngestPerMinute` | Ingest requests per minute per integration key; above it 429 with `Retry-After`. | `600` | ingest | plain |
| `Limits__ApiPerMinutePerUser` | Application API requests per minute per credential (else per IP). | `600` | api | plain |
| `Limits__LoginPerMinutePerIp` | `POST /auth/login` attempts per minute per source IP. | `10` | api | plain |

No host reads `Limits__IngestRps`, which the Helm chart rendered from `limits.ingestRps`. The ingest rate is `Limits__IngestPerMinute`.

**Retention** (spec §18.2, 05 §7; run by the scheduler, shown on the hub health screen)

| Variable | Meaning | Default | Hosts | Secret |
|---|---|---|---|---|
| `Retention__RawDays` | Raw payload partitions older than this are dropped. | `30` | workers, api | plain |
| `Retention__NormalisedClosedDays` | Normalised events of closed (or no) episodes older than this are deleted. | `90` | workers, api | plain |
| `Retention__EpisodeClosedMonths` | Closed episodes and their timeline older than this are deleted; open episodes never are. | `12` | workers, api | plain |
| `Retention__DeliveryAttemptDays` | Delivery attempts, terminal outbox rows and done jobs. | `30` | workers, api | plain |
| `Retention__AuditMonths` | Audit entries. | `12` | workers, api | plain |
| `Retention__SessionExpiredDays` | Sessions, counted from their expiry. | `7` | workers, api | plain |
| `Retention__LoginAttemptDays` | Login attempts. | `30` | workers, api | plain |
| `Retention__BatchSize` | Rows deleted per statement. | `5000` | workers | plain |
| `Retention__RunAtUtcHour` | Daily run time, UTC hour. | `2` | workers, api | plain |

**Local sign-in** (ADR-14; OIDC, milestone 11b, will add its own `Auth__Oidc__*` variables)

| Variable | Meaning | Default | Hosts | Secret |
|---|---|---|---|---|
| `Auth__Local__PasswordMinLength` | Minimum password length. | `12` | api | plain |
| `Auth__Local__LockoutFailures` | Failed logins per user and per IP before lockout. | `10` | api | plain |
| `Auth__Local__LockoutWindow` | Lockout window. | `00:15:00` | api | plain |
| `Auth__Local__SelfServiceResetEnabled` | Offer password reset by e-mail; only effective when `Smtp__Host` is set too. | `false` | api | plain |
| `Auth__Local__SessionSliding` | Sliding session lifetime. | `08:00:00` | api | plain |
| `Auth__Local__SessionAbsolute` | Absolute session lifetime. | `1.00:00:00` | api | plain |

**Notifications and SMTP** (ADR-7; the `Smtp__*` variables only matter when `Smtp__Host` is set)

| Variable | Meaning | Default | Hosts | Secret |
|---|---|---|---|---|
| `Notifications__AllowInsecureDestinations` | Allow `http://` destination URLs. Development only. | `false` | api, workers | plain |
| `Smtp__Host` | SMTP relay. Unset turns the e-mail channel off: e-mail deliveries fail as retryable with *SMTP host not configured*. | unset | workers, api | plain |
| `Smtp__Port` | Relay port. | `587` | workers, api | plain |
| `Smtp__From` | Sender address. | `denoise@example.invalid` | workers, api | plain |
| `Smtp__Security` | `Auto`, `None` (Mailpit/dev), `StartTls` or `SslOnConnect`. | `Auto` | workers, api | plain |
| `Smtp__MessageIdDomain` | Domain of generated `Message-ID`s. | `denoise.local` | workers, api | plain |
| `Smtp__Username` | Relay login; unset = no authentication. | unset | workers, api | plain |
| `Smtp__Password` | Relay password. | unset | workers, api | secret |

**Tuning** (the defaults are the intended values; change them only after measuring)

| Variable | Meaning | Default | Hosts | Secret |
|---|---|---|---|---|
| `Workers__Roles` | Worker roles when no `--roles=` argument is given. | all three | workers | plain |
| `JobQueue__Lease`, `JobQueue__BatchSize`, `JobQueue__IdlePoll`, `JobQueue__ReapInterval` | Job claim lease, jobs per claim, idle poll interval, reaper cadence (05 §3, §7). | `00:02:00`, `20`, `00:00:01`, `00:00:30` | workers | plain |
| `Lifecycle__ProcessingDelayThreshold`, `Lifecycle__MappingFailureThreshold`, `Lifecycle__MappingFailureWindow`, `Lifecycle__PostponeBy` | Auto-resolve guards 4 and 5 and their retry delay (04 §5.3). | `00:05:00`, `5`, `00:15:00`, `00:05:00` | workers | plain |
| `Heartbeats__RunRingSize`, `Heartbeats__BodyBytes`, `Heartbeats__PingsPerMinutePerKey` | Stored runs per heartbeat, stored ping body size, ping rate per key (06 §3). | `100`, `16384`, `60` | ingest, workers | plain |
| `Notifications__MaxAttempts` | Delivery attempts when a destination has no retry policy of its own. | `10` | workers | plain |
| `DeNoise__DeadmanInterval`, `DeNoise__DeadmanTimeout` | Dead-man ping interval and request timeout. | `00:01:00`, `00:00:10` | workers | plain |
| `Pilot__DivergenceThreshold`, `Pilot__SampleSize` | Shadow-mode divergence gate and listed sample size (spec §21). | `0.05`, `20` | api | plain |

The migrator needs only `ConnectionStrings__DeNoise`. It accepts the plain set without effect and must not get the Data Protection keys or the bootstrap or SMTP passwords.

The `api` image also serves the web UI: `build/Dockerfile` builds `web/` (`pnpm build`) into `wwwroot` for `HOST=Api` only, and the Api falls back to `index.html` for every non-file path outside `/api`, `/auth`, `/healthz` and `/openapi`. The SPA therefore needs no variable or ingress route of its own: the public host of `DeNoise__PublicBaseUrl` routes everything to `api`, which keeps the session cookie, CSRF and SSE same-origin.
