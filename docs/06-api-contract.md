# 06 — API Contract

Two hosts. **Ingest host** (public, minimal): `/ingest/*`, `/hb/*`, `/healthz/*`. **Application host** (authenticated): `/api/v1/*`, `/healthz/*`. The OpenAPI document generated from `DeNoise.Api` is the executable contract; this file fixes what it must contain.

## 1. Conventions (application API)

- JSON, `camelCase`, RFC 3339 UTC timestamps, durations as ISO 8601 (`PT15M`).
- Errors: RFC 9457 `application/problem+json` with `type` URN (`urn:denoise:error:version-conflict`), `status`, `title`, `detail`, `traceId`, and `errors[]` for validation.
- Pagination: cursor-based. Request `?limit=50&cursor=…`; response `{ items, nextCursor, total? }` (`total` only when `?includeTotal=true`, capped at 10,000).
- Filtering: repeatable query params, e.g. `?severity=critical&severity=high&handling=new&team=…&scope=…&q=text`. Sorting: `?sort=-severity,lastSeen`.
- Optimistic concurrency: mutating episode/heartbeat/config endpoints require `If-Match: "<version>"`; mismatch ⇒ `409` with current representation in `detail`.
- Idempotency: `Idempotency-Key` header (UUID) required on all `POST` actions; stored 24 h per user; replay returns the original response.
- Rate limits (initial): application API 600 req/min/user; bulk ≤ 200 items per call.
- Every response carries `X-Trace-Id`.
- Authorization: session cookie (UI) or `Authorization: Bearer ah_pat_<keyId>.<secret>` (personal access token, API clients). Roles and scopes come from `cfg.user`. Scope enforcement in every query.

## 2. Ingestion

```
POST /ingest/{integrationKeyId}
  Authorization: Bearer <integration token>        (all types)
  X-MMS-Signature (type=atlas; HMAC over raw body with hmac_secret, algorithm per integration config — see 07 §3; reject if hmac.required and invalid)
  Content-Type: any (stored as-is)
  Body ≤ limits.payloadBytes (default 256 KiB)

  202 { "eventId": "…", "receivedAt": "…" }
  401 invalid token/key            413 too large
  429 rate limited (per integration, default 600/min; Retry-After)
  503 durable acceptance failed (no commit) — sources retry
```
No other status codes. Never `200` before commit. Response body is fixed-size; no echo of input.

Optional `X-DeNoise-Source-Time: <RFC3339>` header lets webhook producers supply `occurred_at` when the body has none.

## 3. Heartbeat ping (ingest host)

```
GET|POST /hb/{keyId}.{token}            success
POST     /hb/{keyId}.{token}/start      run started
POST     /hb/{keyId}.{token}/fail       explicit failure
POST     /hb/{keyId}.{token}/exit/{code}
  Body (optional) ≤ 16 KiB text → stored as run body (tail-truncated)
  200 { "ok": true }        404 unknown/rotated (constant-time compare; no distinction leaked)
  429 per-key limit (default 60/min)
```
`{keyId}` is a 12-char non-secret prefix for lookup; `{token}` is the secret. Both are shown once to the user as a full URL.

## 4. Application API — endpoints

### Episodes
```
GET    /api/v1/episodes                         work queue; filters §1; view=needsAttention|mine|myTeams|unassigned|acknowledged|stale|suppressed|closed
GET    /api/v1/episodes/{id}                    detail incl. identityComponents, timers, routing explanation, coverage state
GET    /api/v1/episodes/{id}/timeline           episode_event + delivery status, paged
GET    /api/v1/episodes/{id}/raw/{eventId}      raw payload (integration_admin+)
GET    /api/v1/episodes/{id}/related            previous/next episodes, group members
POST   /api/v1/episodes/{id}/ack                { force?: bool }
POST   /api/v1/episodes/{id}/assign             { teamId?, userId? }
POST   /api/v1/episodes/{id}/note               { text }
POST   /api/v1/episodes/{id}/close              { reason }  (required)
POST   /api/v1/episodes/{id}/restore            { reason }
POST   /api/v1/episodes/{id}/silence            { until, reason }
POST   /api/v1/episodes/{id}/refresh-state      triggers verify_state job; 202
POST   /api/v1/episodes/bulk                    { ids[], action, params } → { results: [{id, ok, problem?}] }  per-item auth+audit
```

### Auth (application host)
```
POST /auth/login                    { username, password } → 204 + session cookie | 401 (same for unknown user and wrong password) | 423 locked
POST /auth/logout                   → 204, session revoked
POST /auth/change-password          { current, new }   also used for forced change
POST /auth/reset/request            { email } → 202 always   (self-service; only when SMTP reset is enabled)
POST /auth/reset/confirm            { token, new }
GET  /auth/providers                → { local: true, oidc: null | { displayName, loginUrl } }   UI decides which buttons to show
GET  /auth/csrf                     → token for mutating requests from the SPA
```
Rate limit: 10 login attempts / min / IP. All auth events audited (`auth.login`, `auth.login_failed`, `auth.locked`, `auth.logout`, `auth.password_changed`, `auth.reset_*`).

### Users and tokens (platform_admin unless noted)
```
GET/POST /api/v1/users ; GET/PUT /api/v1/users/{id} ; POST /api/v1/users/{id}/disable | enable | unlock
POST /api/v1/users/{id}/reset-password           → { temporaryPassword }  shown once; sets mustChangePassword
GET/POST /api/v1/me/tokens ; DELETE /api/v1/me/tokens/{id}     personal access tokens (any user); POST returns the token once
GET  /api/v1/me/sessions ; DELETE /api/v1/me/sessions/{id}
```

### Teams, users, me
```
GET  /api/v1/me                                 roles, scopes, teams, mustChangePassword
GET/POST/DELETE /api/v1/me/filters              saved queue filters (name, url query)
GET  /api/v1/teams  /api/v1/teams/{id}  /api/v1/teams/{id}/overview   (unassigned, overdue, ageing, stale counts + lists)
POST /api/v1/teams  PUT /api/v1/teams/{id}  (integration_admin+)
GET  /api/v1/users?q=                           directory lookup for assign
```

### Heartbeats
```
GET    /api/v1/heartbeats                        filters: state, team, scope, integration
POST   /api/v1/heartbeats                        → 201 { heartbeat, pingUrl }   (pingUrl contains the token — only time it is returned)
GET    /api/v1/heartbeats/{id}                   state, expectedNext, lastRuns[]
PUT    /api/v1/heartbeats/{id}                   If-Match
DELETE /api/v1/heartbeats/{id}
POST   /api/v1/heartbeats/{id}/pause             { reason }
POST   /api/v1/heartbeats/{id}/resume
POST   /api/v1/heartbeats/{id}/rotate-token      → { pingUrl }
GET    /api/v1/heartbeats/export?team=           YAML
POST   /api/v1/heartbeats/import                 YAML; { dryRun: bool } → diff
```

### Integrations, mappings, replay
```
GET/POST /api/v1/integrations ; GET/PUT /api/v1/integrations/{id}
POST /api/v1/integrations/{id}/rotate-ingest-token        → token shown once
GET  /api/v1/integrations/{id}/health                     coverage state, last signals, backlog, auth status, mapping failure rate
GET  /api/v1/integrations/{id}/mappings                   versions
POST /api/v1/integrations/{id}/mappings                   new version (not active)  { yaml }
POST /api/v1/integrations/{id}/mappings/{v}/preview       { rawEventIds[] | body } → normalised events + identity + routing outcome, no state change
POST /api/v1/integrations/{id}/mappings/{v}/activate
GET  /api/v1/integrations/{id}/failures                   mapping_failure queue
POST /api/v1/replay                                       { mode: preview|retry_failed|historical, integrationId, eventIds? | from,to, mappingVersion? } → job id
GET  /api/v1/replay/{jobId}
```

### Policies (all versioned, same shape)
```
GET  /api/v1/policies/{kind}                              kind ∈ routing|escalation|lifecycle|grouping
POST /api/v1/policies/{kind}                              { yaml } → new version
POST /api/v1/policies/{kind}/{id}/{v}/impact              → { affectedOpenEpisodes: n, sample[] }  (lifecycle/routing)
POST /api/v1/policies/{kind}/{id}/{v}/activate
POST /api/v1/policies/{kind}/{id}/rollback                { toVersion }
GET/POST/DELETE /api/v1/suppressions                      silences + maintenance windows
```

### Destinations
```
GET/POST /api/v1/destinations ; PUT /api/v1/destinations/{id}
POST /api/v1/destinations/{id}/test                       sends a test card/email → delivery outcome
```

### History, audit, hub health
```
GET /api/v1/history?…                                     closed episodes; same filters + closureReason, evidence
GET /api/v1/audit?target=…&actor=…&from=&to=
GET /api/v1/hub/health                                    components, queue depths, oldest job age, outbox lag, failed queue counts, deadman status
GET /api/v1/hub/failures                                  permanently failed jobs/outbox (platform_admin)
POST /api/v1/hub/failures/{id}/retry
```

### Realtime
```
GET /api/v1/events/stream?scope=…                         text/event-stream
  event: episode.changed   data: { id, version, handling, condition, severity, teamId }
  event: coverage.changed  data: { integrationId, state }
  event: heartbeat.changed data: { id, state }
  event: hub.health        data: { degraded: bool }
  id: <monotonic per scope>; server replays from Last-Event-ID within 5 min, else sends event: resync
  heartbeat comment every 15 s
```

## 5. Key response shapes

**Episode (list item)**
```json
{ "id":"…","severity":"critical","summary":"…","resourceName":"…","service":"…","environment":"production",
  "conditionState":"firing","handlingState":"new","owningTeam":{"id":"…","name":"…"},"assignee":null,
  "firstSeen":"…","lastSeen":"…","occurrenceCount":14,"occurrenceCountExact":true,
  "ackDeadlineAt":"…","ackOverdue":true,"nextEscalationAt":"…","autoResolveAt":null,"autoResolveSuspended":true,
  "coverageState":"degraded","stale":true,"suppressedUntil":null,"deliveryFailure":false,
  "groupId":null,"routingCorrectionRequired":false,"version":7 }
```
**Episode detail adds:** `identityComponents`, `lifecyclePolicy` (resolved values), `routing: { ruleId, ruleName, why }`, `closure: { reason, evidence, note, at, by }`, `sourceUrl`, `runbookUrl`, `rawPayloadAvailable`, `previousEpisodeId`, `timers[]`, `explanation` (plain-language paragraph built server-side).

## 6. Auth flows

- UI: `POST /auth/login` → server-side session (`cfg.session`) → cookie `HttpOnly; Secure; SameSite=Lax`, sliding 8 h, absolute 24 h. CSRF double-submit token on mutations. `mustChangePassword` in `/me` forces the change screen before any route.
- API clients / CI: personal access token as bearer; same permission checks; `last_used_at` updated.
- Bootstrap: first start with no users seeds `admin` from `auth.local.bootstrapPassword` (Helm secret) with `must_change_password=true`; the value is never logged.
- OIDC (later): `GET /auth/providers` advertises it; `/auth/oidc/login` and `/auth/oidc/callback` are reserved; users matched on email; permissions untouched (ADR-14).

## 7. Notification payloads

**Outbound webhook (primary channel).** Request as in ADR-7: `POST <url>`, headers `X-DeNoise-Delivery-Id`, `X-DeNoise-Event`, `X-DeNoise-Timestamp`, `X-DeNoise-Signature: v1=<hmac-sha256>`, plus destination headers. Body = rendered template. Receiver verification recipe (documented in UI): `hmac_sha256(secret, timestamp + "." + rawBody) == signature` and reject if `|now − timestamp| > 300 s`.

**Notification model** available to templates (and the body of the `generic-json` built-in):
```json
{ "event": "episode.opened", "deliveryId": "…", "sentAt": "…",
  "episode": { "id":"…", "url":"https://hub/episodes/…", "severity":"critical", "summary":"…",
    "conditionState":"firing", "handlingState":"new", "resource": {"id":"…","name":"…"}, "rule": {"id":"…","name":"…"},
    "service":"…", "environment":"production", "integration": {"id":"…","name":"…","type":"azure_monitor"},
    "owningTeam": {"id":"…","name":"…"}, "assignee": null, "firstSeen":"…", "lastSeen":"…", "occurrenceCount": 3,
    "ackDeadlineAt":"…", "sourceUrl":"…", "runbookUrl":"…",
    "closure": null | { "reason":"inactivity_timeout", "evidence":"inactivity_unverified", "at":"…", "by": null },
    "coverageState":"healthy", "groupId": null, "labels": {…}, "dimensions": {…} },
  "escalation": null | { "step": 2, "reason": "ack_overdue" },
  "previous": null | { "severity": "high" } }
```
Text fields are raw strings; templates in `json` format escape them automatically, `text` templates must use `{{ x | escape }}` where the receiver expects HTML. Coverage and heartbeat notifications use the same envelope with `coverage` / `heartbeat` objects instead of `episode`.

**Built-in templates:** `generic-json`, `teams-adaptive-card` (Adaptive Card 1.5 inside `{ "type":"message", "attachments":[…] }` for a Power Automate Workflows trigger), `slack-blocks`, `plain-text`. Endpoints:
```
GET  /api/v1/webhook-templates ; GET /api/v1/webhook-templates/{id}/{v}
POST /api/v1/webhook-templates                   { name, format, body, contentType, sampleEvent? } → new version (validates: parses, renders sample, output size)
POST /api/v1/webhook-templates/{id}/{v}/render   { episodeId | sampleEvent } → rendered body (preview, no send)
POST /api/v1/webhook-templates/{id}/{v}/activate
```
Destinations: `POST /api/v1/destinations/{id}/test` sends a synthetic `episode.opened` to the real URL and returns status, latency, response excerpt. `GET /api/v1/destinations/{id}/deliveries` lists recent attempts.

**Email:** subject `[DeNoise][{severity}] {summary} — {resourceName}`; plain-text body with the same fields and links; `Message-ID` derived from `outbox_id` for destination-side dedup; `References` header links updates to the opening mail.

## 8. Health endpoints (both hosts)

`/healthz/live` (process up), `/healthz/ready` (DB reachable, migrations at expected version, on api: outbox lag < threshold), `/healthz/startup`. Workers expose the same on a side port.

## 9. Export formats

All config `export` endpoints return YAML matching the DSL in 07; `import` accepts the same and returns a structured diff before activation.
