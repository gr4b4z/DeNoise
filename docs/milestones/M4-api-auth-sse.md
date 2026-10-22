# Milestone 4 (backend) — Application API, local auth, SSE

Status: **backend done**; the React shell, queue and detail screens (08) follow as milestone 4b in the same branch.

## What exists

| Area | Where | Notes |
|---|---|---|
| Users, sessions, tokens | `cfg."user"`, `cfg.session`, `cfg.personal_access_token`, `cfg.login_attempt`, `cfg.team_member`, `cfg.saved_filter`, `cfg.idempotency_record` | Migration `UsersAndAuth`. Roles and scopes live on the user regardless of provider (ADR-14). Also creates `ops.coverage_state` (05 §3) so the read model can join it before milestone 5 drives it. |
| Local identity provider | `Application/Auth/LocalPasswordProvider` | Argon2id (m=64 MiB, t=3) via `ISecretHasher`; unknown user and wrong password take the same path (dummy hash verified) and return the same 401; lockout **10 failures / 15 min per user and per IP** — the tenth failure is still a 401, the eleventh attempt is 423 with `lockedUntil`; per-IP lockout is counted from `cfg.login_attempt` and is independent of an admin unlock. |
| Sessions | `AuthService` | Server-side, revocable; the cookie carries a random token, the table stores its SHA-256; sliding 8 h / absolute 24 h; `HttpOnly Secure SameSite=Lax`; `/api/v1/me/sessions` lists and revokes. |
| Password policy | `PasswordPolicy`, `LocalAuthOptions` | Min 12 chars, bundled breached list (422 `breached`), forced change on first login (`MustChangePasswordFilter` → 403 `password-change-required` on everything except `/auth/*` and `/api/v1/me`), admin reset with a one-time temporary password. |
| Bootstrap admin | `AuthService.BootstrapAdminIfEmptyAsync`, `Program.cs` | Runs on API start when `cfg."user"` is empty; password from `Auth:Local:BootstrapPassword` or generated and printed **once to stdout**, never logged nor audited. |
| Personal access tokens | `PersonalAccessTokenService` | `ah_pat_<keyId>.<secret>`; Argon2id-hashed secret, key-id lookup (ADR-11), optional expiry, revocation, permissions capped at the owner's; bearer requests skip CSRF. |
| Authentication scheme | `Api/Auth/DeNoiseAuthenticationHandler` | One scheme resolving cookie *or* bearer to an `DeNoisePrincipal` (roles, scopes, permission set, `MustChangePassword`). |
| RBAC | `Domain/Users/Rbac`, `RequirePermissionFilter` | The 04 §8 matrix as a permission set per role; endpoints declare the permission; scope checks return **404** for anything outside the caller's scopes (existence never leaks). |
| CSRF | `Api/Auth/Csrf` | Data-Protection token bound to the session, fetched from `GET /auth/csrf`, required as `X-CSRF-Token` on every cookie-authenticated mutation (403 `csrf`). |
| Mutation contract | `IfMatchFilter`, `IdempotencyFilter` | `If-Match: "<version>"` mandatory (428 `precondition-required`), mismatch ⇒ 409 `version-conflict` with `current` detail; `Idempotency-Key` (UUID) mandatory on actions (400), replay returns the stored body byte-for-byte with `Idempotent-Replayed: true`, reuse with another payload ⇒ 422. Records expire after 24 h. |
| Problems | `Api/Auth/Problems` | RFC 9457, `type = urn:denoise:error:<code>`, `traceId`, `instance`; 500 hides the message and logs it under the trace id. |
| Episode read model | `Infrastructure/ReadModels/EpisodeQueries` | Raw SQL over `alert.episode` with team, assignee, integration, next escalation, suspended auto-resolve, delivery failure and coverage state; queue views `needsAttention`, `mine`, `myTeams`, `unassigned`, `closed`, `all`; filters (severity, team, integration, environment, service, assignee, text search on `search_tsv`); keyset cursor on (severity rank, ack overdue, last seen, id); `includeTotal`; detail with identity components, lifecycle, routing `why`, closure block, timers, plain-language `explanation`. |
| Episode actions | `Application/Episodes/EpisodeActionService` | ack, assign (`force` for takeover, assignment never resets the ack deadline), note, close (reason mandatory, condition untouched), restore (only `manual_close`/`expired_unverified`, refused while a newer episode for the identity is open), silence (>24 h needs `integration_admin`), refresh-state (schedules `verify_state`); all inside the processing unit of work with timeline + audit rows; concurrent losers get 409 with the winner's version. Bulk: up to 200 ids, per-item results. |
| Admin and config API | `MeAndUsersEndpoints`, `ConfigEndpoints` | `/api/v1/me`, `/me/tokens`, `/me/sessions`, `/me/password`; `/users` CRUD + reset/unlock/disable; `/teams` (+ `overview`), `/integrations`, `/policies/{kind}`, `/destinations` read/write per the RBAC matrix. |
| Live updates | `Infrastructure/Realtime/PgChangeBus`, `SseHub`, `EventsEndpoints` | `GET /api/v1/events/stream` (ADR-12): scope-filtered `episode.changed` events with ids, 5 min ring buffer, `Last-Event-ID` replay, `resync` when the id is unknown or from another process generation, heartbeat comments; fan-out across API replicas via PostgreSQL `LISTEN/NOTIFY` on `denoise_changes`. |
| Rate limits | `Program.cs` | 600 req/min per user (or IP), 10 login attempts / min / IP (06 §1, §4); `X-Forwarded-For` honoured for the in-cluster ingress. |
| OpenAPI | `docs/openapi/v1.json`, `Contract.Tests` | Generated from the running API; `session` and `pat` security schemes; every non-anonymous operation declares security; the committed snapshot is compared on every test run (`UPDATE_OPENAPI=1` regenerates). |

## Acceptance scenarios (spec §22)

| Scenario | Test |
|---|---|
| Two users take ownership | `EpisodeApiTests.Scenario_TwoUsersTakeOwnership_*` — concurrent acks with the same `If-Match`; one 200, one 409 whose body carries the current item (version 2, acknowledged, assignee). |
| User lacks resource access | `EpisodeApiTests.Scenario_UserLacksResourceAccess_*` — list omits scope-b, detail/timeline/actions on a scope-b id are 404 for operator and viewer alike; bulk reports 404 per item. |
| Manual close while source is firing (API half) | `EpisodeApiTests.Scenario_ManualCloseWhileSourceIsFiring_api_*` — reason mandatory, condition stays `firing`, closure block with `human` evidence, closed view, successor episode linked via `previousEpisodeId`, restore refused on the superseded episode and working on the closed successor. |
| UI loses its live connection (server half) | `SseTests.Episode_changes_reach_subscribers_in_scope_with_ids_and_replay_after_reconnect` and `Unknown_or_foreign_last_event_id_triggers_resync` — the browser half is a Playwright test in 4b. |

Auth coverage in `AuthApiTests`: same 401 for unknown user and wrong password, eleventh failure locks + admin unlock, independent per-IP lockout, forced change on first login with breached-list rejection, PAT scope cap + revocation + expiry, session revoke, CSRF for cookies but not bearer, bootstrap password never persisted.

## Decisions made

- **Per-IP lockout stays locked after an account unlock.** Unlocking a user answers "my colleague locked herself out"; it must not also reopen the door for whoever produced the failures.
- **The tenth failure is a 401, not a 423.** The account is locked *as a consequence* of ten failures; the caller learns about it on the next attempt. Keeps the "same response for unknown user and wrong password" property intact up to the threshold.
- **Idempotent replays are serialised with the host's JSON options** so the stored copy is byte-identical to the original response (relaxed escaping of `→`, `∅`, quotes).
- **Restore is refused while a successor is open.** The one-open-episode-per-identity index would reject it anyway; the API now says so (409 `invalid-transition` naming the successor) instead of a version conflict.
- **404 for out-of-scope episodes everywhere**, including bulk item results and SSE (events for other scopes are simply not delivered).
- **SSE ids are `<generation>-<sequence>`**; an id from another process generation forces a `resync` rather than a silent gap.
- **`ops.coverage_state` is created now** (empty until milestone 5) so the detail contract already carries `coverageState` (`unknown` when no row exists).

## Running

```
dotnet test                           # contract tests need no database
UPDATE_OPENAPI=1 dotnet test tests/DeNoise.Contract.Tests   # refresh docs/openapi/v1.json
```

Integration tests use Testcontainers, or `DENOISE_TEST_CONNECTION` to point at an existing PostgreSQL.
