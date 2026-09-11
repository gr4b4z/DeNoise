# 05 — Data Schema (PostgreSQL)

DDL is a sketch: EF Core migrations are the executable form, but the **constraints marked ★ are invariants** and must exist exactly as stated. Schemas: `alert`, `hb`, `ops`, `audit`, `cfg`.

## 1. Conventions

- Primary keys: `uuid` (UUIDv7, generated in app for time-ordering).
- All timestamps `timestamptz`. All durations stored as `interval`.
- `jsonb` for payloads and components; never for anything that is filtered on without an expression index.
- Soft delete only where the spec needs history (`cfg.*` versions). Everything else is append-only or hard-deleted by retention.
- Every table has `created_at`; mutable tables have `updated_at` and `version int` (optimistic concurrency).

## 2. `alert` schema

```sql
CREATE TABLE alert.raw_event (
  event_id        uuid        NOT NULL,
  integration_id  uuid        NOT NULL,
  received_at     timestamptz NOT NULL,
  content_type    text,
  body            bytea       NOT NULL,          -- raw bytes, may be invalid JSON
  headers         jsonb,                          -- allow-listed headers only
  source_ip       inet,
  size_bytes      int         NOT NULL,
  PRIMARY KEY (received_at, event_id)
) PARTITION BY RANGE (received_at);
-- daily partitions, created 7 days ahead by ops job; dropped by retention_raw (30 d)

CREATE TABLE alert.applied_event (            -- delivery idempotency ledger
  integration_id  uuid NOT NULL,
  delivery_key    text NOT NULL,
  event_id        uuid NOT NULL,
  applied_at      timestamptz NOT NULL,
  outcome         text NOT NULL,                 -- applied | late | replayed | mapping_failed
  PRIMARY KEY (integration_id, delivery_key)     -- ★ duplicates rejected here, nowhere else
);

CREATE TABLE alert.delivery_duplicate (
  integration_id uuid, delivery_key text, count int NOT NULL DEFAULT 1,
  first_seen timestamptz, last_seen timestamptz,
  PRIMARY KEY (integration_id, delivery_key)
);

CREATE TABLE alert.normalised_event (
  event_id         uuid PRIMARY KEY,
  integration_id   uuid NOT NULL,
  raw_received_at  timestamptz NOT NULL,          -- for join to partitioned raw
  mapping_version  int  NOT NULL,
  event_type       text NOT NULL,                 -- firing|update|resolved|acknowledged|cancelled|informational|heartbeat
  source_alert_id  text, source_event_id text, source_version text,
  occurred_at      timestamptz, received_at timestamptz NOT NULL,
  severity         text NOT NULL, source_severity text,
  resource_id text, resource_name text, rule_id text, rule_name text,
  environment text, service text, summary text, source_url text,
  dimensions jsonb, labels jsonb,
  fingerprint      bytea,                         -- null for heartbeat/informational-without-identity
  identity_components jsonb,
  delivery_key     text NOT NULL,
  episode_id       uuid                           -- set when applied
);
CREATE INDEX ON alert.normalised_event (integration_id, received_at DESC);
CREATE INDEX ON alert.normalised_event (episode_id);

CREATE TABLE alert.identity (
  fingerprint      bytea PRIMARY KEY,
  integration_id   uuid NOT NULL,
  access_scope     text NOT NULL,
  identity_version int  NOT NULL,
  components       jsonb NOT NULL,
  first_seen       timestamptz NOT NULL,
  episode_count    int NOT NULL DEFAULT 0
);

CREATE TABLE alert.episode (
  episode_id        uuid PRIMARY KEY,
  fingerprint       bytea NOT NULL REFERENCES alert.identity,
  integration_id    uuid NOT NULL,
  access_scope      text NOT NULL,
  previous_episode_id uuid,
  condition_state   text NOT NULL,   -- firing|resolved|unknown|not_applicable
  handling_state    text NOT NULL,   -- new|acknowledged|closed
  severity          text NOT NULL,
  is_actionable     bool NOT NULL,
  first_seen        timestamptz NOT NULL,
  last_seen         timestamptz NOT NULL,
  last_received_at  timestamptz NOT NULL,
  last_verified_at  timestamptz,
  last_applied_version text,
  occurrence_count  int NOT NULL DEFAULT 1,
  owning_team_id    uuid NOT NULL,
  assignee_id       uuid,
  routing_rule_id   uuid,
  routing_correction_required bool NOT NULL DEFAULT false,
  ack_deadline_at   timestamptz, acknowledged_at timestamptz, acknowledged_by uuid,
  follow_up_at      timestamptz,
  auto_resolve_at   timestamptz, lifecycle_policy_version int,
  closed_at         timestamptz, closure_reason text, resolution_evidence text,
  closure_note      text, restored_from_reason text,
  suppressed_until  timestamptz, suppression_source text,
  group_id          uuid,
  summary text, resource_name text, rule_name text, service text, environment text, source_url text,
  runbook_url text,
  search_tsv        tsvector GENERATED ALWAYS AS (
                      to_tsvector('simple', coalesce(summary,'')||' '||coalesce(resource_name,'')||' '||coalesce(rule_name,'')||' '||coalesce(service,''))) STORED,
  version           int NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL
);
-- ★ exactly one open episode per identity
CREATE UNIQUE INDEX episode_one_open_per_identity
  ON alert.episode (fingerprint) WHERE handling_state <> 'closed';
CREATE INDEX episode_queue_idx ON alert.episode (access_scope, handling_state, severity, last_seen DESC) WHERE handling_state <> 'closed';
CREATE INDEX episode_team_idx  ON alert.episode (owning_team_id, handling_state, ack_deadline_at);
CREATE INDEX episode_search_idx ON alert.episode USING gin (search_tsv);
CREATE INDEX episode_closed_idx ON alert.episode (closed_at) WHERE handling_state = 'closed';

CREATE TABLE alert.episode_event (              -- timeline
  id uuid PRIMARY KEY, episode_id uuid NOT NULL REFERENCES alert.episode,
  at timestamptz NOT NULL, kind text NOT NULL,   -- source_event|late_event|replayed|ack|assign|takeover|note|close|restore|auto_resolve|suspend|resume|notify|escalate|suppress
  actor_id uuid, event_id uuid, detail jsonb
);
CREATE INDEX ON alert.episode_event (episode_id, at);

CREATE TABLE alert.source_instance_state (    -- closed-before-open markers
  integration_id uuid, source_alert_id text, resolved_at timestamptz NOT NULL,
  PRIMARY KEY (integration_id, source_alert_id)
);

CREATE TABLE alert.alert_group (
  group_id uuid PRIMARY KEY, access_scope text NOT NULL, rule_id uuid NOT NULL,
  key_values jsonb NOT NULL, opened_at timestamptz NOT NULL, window_ends_at timestamptz NOT NULL,
  severity text NOT NULL, member_count int NOT NULL, closed_at timestamptz
);
CREATE UNIQUE INDEX ON alert.alert_group (rule_id, key_values) WHERE closed_at IS NULL;

CREATE TABLE alert.mapping_failure (
  id uuid PRIMARY KEY, integration_id uuid NOT NULL, event_id uuid NOT NULL,
  raw_received_at timestamptz NOT NULL, mapping_version int, error text NOT NULL,
  field text, quarantined bool NOT NULL DEFAULT true, resolved_at timestamptz, resolved_by uuid
);
```

## 3. `ops` schema

```sql
CREATE TABLE ops.job (
  job_id uuid PRIMARY KEY, kind text NOT NULL, status text NOT NULL,
  not_before timestamptz NOT NULL, priority smallint NOT NULL DEFAULT 100,
  payload jsonb NOT NULL, episode_id uuid, integration_id uuid,
  expected_version int, expected_last_seen timestamptz,
  attempts int NOT NULL DEFAULT 0, max_attempts int NOT NULL DEFAULT 10,
  reserved_by text, reserved_until timestamptz, last_error text,
  created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL
) WITH (fillfactor = 70);
CREATE INDEX job_claim_idx ON ops.job (kind, not_before) WHERE status = 'pending';
CREATE INDEX job_episode_idx ON ops.job (episode_id, kind) WHERE status IN ('pending','suspended');
-- ★ one live timer of each kind per episode
CREATE UNIQUE INDEX job_one_timer_per_episode ON ops.job (episode_id, kind)
  WHERE status IN ('pending','reserved','suspended') AND episode_id IS NOT NULL;

-- claim: UPDATE ops.job SET status='reserved', reserved_by=$1, reserved_until=now()+$2, attempts=attempts+1
--        WHERE job_id IN (SELECT job_id FROM ops.job WHERE status='pending' AND kind = ANY($3) AND not_before<=now()
--                         ORDER BY priority, not_before FOR UPDATE SKIP LOCKED LIMIT $4) RETURNING *;
-- complete: UPDATE … SET status='done' WHERE job_id=$1 AND reserved_by=$2 AND reserved_until>now();  -- ★ token check
-- reaper: UPDATE … SET status='pending', reserved_by=NULL WHERE status='reserved' AND reserved_until<now();

CREATE TABLE ops.outbox (
  outbox_id uuid PRIMARY KEY, episode_id uuid, type text NOT NULL,
  destination_id uuid NOT NULL, policy_version int, payload jsonb NOT NULL,
  status text NOT NULL,   -- pending|reserved|sent|failed|coalesced|suppressed|cancelled
  not_before timestamptz NOT NULL, attempts int NOT NULL DEFAULT 0,
  reserved_by text, reserved_until timestamptz, sent_at timestamptz, last_error text,
  created_at timestamptz NOT NULL
) WITH (fillfactor = 70);
CREATE INDEX outbox_claim_idx ON ops.outbox (not_before) WHERE status = 'pending';
CREATE INDEX outbox_episode_idx ON ops.outbox (episode_id, created_at);

CREATE TABLE ops.delivery_attempt (
  id uuid PRIMARY KEY, outbox_id uuid NOT NULL, attempted_at timestamptz NOT NULL,
  channel text NOT NULL, outcome text NOT NULL,   -- success|retryable|permanent|response_lost
  http_status int, latency_ms int, error text, used_fallback bool NOT NULL DEFAULT false,
  response_excerpt text                          -- first 4 KiB of the receiver response
);
CREATE INDEX ON ops.delivery_attempt (attempted_at);   -- retention 30 d

CREATE TABLE ops.coverage_state (
  integration_id uuid PRIMARY KEY, state text NOT NULL, since timestamptz NOT NULL,
  last_signal_at timestamptz, consecutive_successes int NOT NULL DEFAULT 0,
  last_processed_alert_at timestamptz, coverage_episode_id uuid, detail jsonb
);

CREATE TABLE ops.hub_component_heartbeat (
  component text PRIMARY KEY, instance text NOT NULL, last_seen timestamptz NOT NULL
);
```

## 4. `audit` schema

```sql
CREATE TABLE audit.entry (
  id uuid PRIMARY KEY, at timestamptz NOT NULL,
  actor_type text NOT NULL,      -- user|system|integration
  actor_id text NOT NULL, actor_display text,
  action text NOT NULL,          -- dotted verb: episode.ack, policy.activate, heartbeat.rotate_token …
  target_type text NOT NULL, target_id text NOT NULL, access_scope text,
  before jsonb, after jsonb, reason text,
  correlation_id text NOT NULL, request_ip inet
);
CREATE INDEX ON audit.entry (target_type, target_id, at DESC);
CREATE INDEX ON audit.entry (actor_id, at DESC);
CREATE INDEX ON audit.entry (at);              -- retention 12 m
```
Audit rows are inserted in the same transaction as the change. No updates, no deletes except retention.

## 5. `hb` schema

```sql
CREATE TABLE hb.heartbeat (
  heartbeat_id uuid PRIMARY KEY, name text NOT NULL, description text,
  access_scope text NOT NULL, owning_team_id uuid NOT NULL, assignee_id uuid,
  schedule_kind text NOT NULL,          -- interval|cron
  interval interval, cron text, schedule_tz text,
  grace interval NOT NULL, severity_on_miss text NOT NULL, routing_policy_id uuid,
  binds_to_integration_id uuid, recovery_successes_required int NOT NULL DEFAULT 1,
  auto_pause_during_maintenance bool NOT NULL DEFAULT true,
  state text NOT NULL DEFAULT 'unknown', expected_next timestamptz,
  last_ping_at timestamptz, last_ping_ip inet, last_run_duration interval, consecutive_successes int NOT NULL DEFAULT 0,
  paused_by uuid, paused_at timestamptz, pause_reason text,
  key_id text NOT NULL UNIQUE,          -- non-secret prefix used for lookup
  token_hash text NOT NULL, token_rotated_at timestamptz NOT NULL,
  miss_episode_id uuid,
  version int NOT NULL DEFAULT 1, created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL
);
CREATE INDEX hb_due_idx ON hb.heartbeat (expected_next) WHERE state IN ('healthy','late');

CREATE TABLE hb.run (                   -- ring of last N, trimmed by trigger or job
  heartbeat_id uuid NOT NULL, seq bigint NOT NULL,
  started_at timestamptz, finished_at timestamptz NOT NULL, kind text NOT NULL,  -- success|fail|start|exit
  exit_code int, body text, source_ip inet,
  PRIMARY KEY (heartbeat_id, seq)
);
-- keep last 100 per heartbeat; trim in the scheduler's heartbeat_check job
```

Pings update `hb.heartbeat` in a single `UPDATE … WHERE key_id=$1` after hash verification and insert one `hb.run` row. No `raw_event`, no `job`.

## 6. `cfg` schema

All tables versioned: `(id, version)` PK, `activated_at`, `deactivated_at`, `created_by`, `source_yaml text`, `body jsonb`.

`cfg.integration` (name, type, access_scope, owner_team_id, ingest_key_id, ingest_token_hash, hmac_secret_enc, ip_allow_list, capabilities jsonb, coverage jsonb, profile_defaults jsonb, active bool)
`cfg.mapping` (integration_id, body jsonb, identity_version, samples jsonb)
`cfg.routing_rule`, `cfg.escalation_policy`, `cfg.lifecycle_policy`, `cfg.grouping_rule`, `cfg.suppression` (non-versioned, time-bounded)
`cfg.team` (name, access_scopes text[], fallback_team_id, coverage_hours jsonb, default_escalation_policy_id, entra_group_ids text[])
`cfg.team_member` (team_id, user_id, role)
`cfg.destination` (team_id, name, channel_type ∈ {webhook, smtp_email}, url_enc, method text DEFAULT 'POST', headers_enc jsonb, body_template_id, signing_secret_enc, timeout interval DEFAULT '10s', event_types text[], retry_policy jsonb, email_to text[], fallback_destination_id NOT NULL, owner_user_id, active, last_success_at, last_failure_at, consecutive_failures int)
`cfg.webhook_template` (template_id, version, name, format ∈ {json, text}, body text, content_type, sample_output text, builtin bool, activated_at, deactivated_at) — built-ins are read-only rows
`cfg.user` (user_id, username UNIQUE, email UNIQUE, display_name, auth_provider text NOT NULL DEFAULT 'local', external_id, password_hash, password_changed_at, must_change_password bool, failed_attempts int, locked_until timestamptz, disabled bool, totp_secret_enc, roles text[], scopes text[], last_login_at, created_by) — roles/scopes are local regardless of provider (ADR-14)
`cfg.session` (session_id PK, user_id, created_at, last_seen_at, expires_at, ip inet, user_agent, revoked_at) — server-side sessions; the cookie carries only the id
`cfg.personal_access_token` (token_id PK, user_id, name, key_id UNIQUE, token_hash, scopes text[], expires_at, last_used_at, revoked_at)
`cfg.login_attempt` (id, at, username, ip inet, success bool) — lockout and audit; 30 d retention
`cfg.password_reset` (id, user_id, token_hash, expires_at, used_at) — only when SMTP self-service reset is enabled
`cfg.scope` (scope text PK, product text, entra_group_ids text[]) — seeded from config (C6)

★ `cfg.destination.fallback_destination_id` NOT NULL and ≠ self (check constraint).
★ Exactly one active version per `(table, id)`: unique partial index `WHERE deactivated_at IS NULL`.

## 7. Retention and maintenance jobs

| Job | Cadence | Action | Safety |
|---|---|---|---|
| `partition_create` | daily | create `alert.raw_event` partitions 7 days ahead | idempotent |
| `retention_raw` | daily 02:00 UTC | `DROP` partitions older than `retention.raw_days` (30) | never drops today−1 or newer; logs partition names to audit |
| `retention_rows` | daily | delete `normalised_event` > 90 d **only where episode closed**; `episode` + `episode_event` > 12 m closed; `delivery_attempt` > 30 d (including stored response bodies); `audit.entry` > 12 m; `hb.run` beyond ring; `cfg.session` expired > 7 d; `cfg.login_attempt` > 30 d | batched 5,000 rows/txn; open episodes never touched |
| `job_reaper` | 30 s | release expired reservations | — |
| `outbox_reaper` | 30 s | same | — |
| `vacuum_hint` | — | rely on autovacuum with per-table settings: `autovacuum_vacuum_scale_factor=0.02` on `ops.job`, `ops.outbox`, `alert.episode` | set in migration |

Open episodes whose raw payload partition has been dropped show "original payload no longer retained" (UI flag from `raw_received_at < retention boundary`).

## 8. Sizing assumptions to validate against the baseline (C9)

Daily partitions assume < 500k raw events/day. If the baseline shows more, switch to hourly partitions **before** first production migration.
