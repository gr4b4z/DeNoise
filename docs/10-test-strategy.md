# 10 — Test Strategy

## 1. Layers

| Layer | Project | Tooling | Runs | Purpose |
|---|---|---|---|---|
| Unit | `Domain.Tests`, `Application.Tests` | xUnit, FluentAssertions, `FakeTimeProvider`, Verify (snapshots for mapping output and explanation text) | every push, < 30 s | state machines, fingerprint canonicalisation, delivery key, ordering, DSL interpreter, predicate engine, cron/DST |
| Integration | `Integration.Tests` | Testcontainers PostgreSQL, real EF + Dapper, in-process hosts via `WebApplicationFactory`, fake channels | every push, < 5 min | **every spec §22 scenario**, concurrency, transactions, jobs, outbox, SSE |
| Contract | `Contract.Tests` | generated OpenAPI vs implementation (Kiota/NSwag diff), schema examples validate | every push | frontend/backend drift |
| Frontend unit | `web` | Vitest + Testing Library, MSW | every push | components, `can()`, SSE cache patching |
| E2E | `web/e2e` | Playwright against compose stack, axe-core | nightly + pre-release | routes, a11y, live-update behaviour, one-time secret dialog |
| Load | `tests/load` | k6 against a staging namespace | pre-pilot, on demand | spec §20.2 targets |
| Chaos/ops | scripts | kill workers mid-job, drop DB connection, block a webhook destination URL | pre-pilot | reservation reaping, outbox retry, fallback |

Coverage target: Domain ≥ 90 % line, Application ≥ 80 %, no target for Infrastructure/UI beyond scenario coverage.

## 2. Acceptance scenario → test mapping

Each row is one or more `[Fact]`/`[Theory]` in `Integration.Tests`, named `Scenario_<Slug>`.

| Scenario (spec §22) | Test approach |
|---|---|
| Duplicate webhook delivery | ingest same body twice; assert 1 applied_event, 1 delivery_duplicate row, count=1, 1 outbox row |
| Two workers process one condition | two claims on two normalise jobs for events with same fingerprint, run concurrently; assert one episode, unique-index conflict handled, both events applied |
| Worker fails during processing | throw after claim; assert reservation reaped and job re-runs; raw event untouched |
| Worker fails after state commit | kill between commit and SSE publish; assert outbox row exists and dispatcher sends |
| Delivery succeeds but response lost | fake channel returns timeout after "sending"; assert attempt `response_lost`, retry with same `Message-ID`/card id, duplicate recorded not hidden |
| Recovery arrives before opening | `resolved` then late `firing` (older occurred_at); assert `source_instance_state`, no episode opened |
| Old recovery during a new episode | episode A closed; new episode B; `resolved` with occurred_at in A's window; assert B firing, event recorded `late` |
| No ownership rule matches | assert team=triage, `routing_correction_required`, `episode.routing_failure` outbox |
| Two users take ownership | concurrent `ack` with same `If-Match`; assert one 200, one 409 with current state |
| Repeating source quiet, coverage healthy | FakeTimeProvider advance; coverage healthy; assert closure `inactivity_timeout` / `heartbeat_and_inactivity` |
| Source quiet, coverage never configured | same, coverage `unknown`; assert evidence `inactivity_unverified` and UI flag field |
| Azure `resolved` lost in transit | explicit_recovery profile, no resolved event; advance 60 min; assert backstop closure, evidence not `source` |
| Operator changes auto-resolve timeout | impact endpoint returns affected count; activation reschedules jobs; none closed instantly |
| Canary rule stops firing | advance past `alert_after`; assert coverage episode, `auto_resolve` jobs `suspended`, condition `unknown`; canary resumes ×3 ⇒ resumed |
| Coverage alert flaps | success/fail alternation below `recovery_successes_required`; assert single coverage episode |
| Ingestion or mapping unhealthy | inject mapping failures above threshold; assert closure postponed |
| Old alert reaches administrative expiry | medium severity, unknown profile; advance 7 d; assert `expired_unverified`, condition preserved, not in queue, in stale view |
| New signal races with expiry | schedule expiry, apply new event, run job; assert not closed (guard 1/2) |
| Source resumes after expiry | new firing after expiry ⇒ new episode with `previous_episode_id` |
| Maintenance ends with active condition | suppression window over open episode; advance past end; assert one summary outbox per team |
| Maintenance crosses DST | window 01:30–03:30 Europe/Warsaw on transition night; assert wall-clock semantics both directions |
| New critical child joins a group | group with medium members; add critical; assert group severity critical, child outbox created |
| Historical replay | replay mode over closed episodes; assert zero transitions, zero outbox, events tagged `replayed` |
| UI loses live connection | Playwright: block SSE; assert FreshnessBar red, data visible, action still works; unblock; `Last-Event-ID` replay |
| Manual close while firing | close; then `firing` update arrives; assert new episode opens (closed is immutable) and previous shows condition `firing` + `manual_close` |
| User lacks resource access | user in scope A requests episode in scope B: 404 via API, route hidden in UI |
| Webhook destination fails permanently | receiver returns 404; assert no further retries, fallback destination used, `used_fallback`, `hub.delivery_failure` |
| Webhook destination fails transiently | receiver returns 503 then 200; assert backoff, identical `X-DeNoise-Delivery-Id`, `Retry-After` honoured |
| Webhook signature | receiver-side recompute of `v1=` over `timestamp.body` matches; tampered body fails |
| Template escaping | summary containing `"},"severity":"low` renders as a valid JSON string in `json` format; `text` format renders verbatim |
| Template sandbox | template referencing undefined member or looping 10⁶ times ⇒ validation error / render timeout, no send |
| Raw payload retention expires on open episode | drop partition; assert episode actionable, `rawPayloadAvailable=false` |
| Hub or monitored cluster fails | dead-man job stops ⇒ external fake receives no ping (asserted via Mailpit-like sink); runbook exists |
| Out-of-hours critical alert | escalation policy with `external` step; assert step dispatched to configured target after ack deadline |
| Registered heartbeat scenarios (spec §22, 9 rows) | one test each: miss ⇒ one episode; many misses ⇒ still one; DST cron; `/fail` immediate; token rotate; pause; maintenance auto-pause; scheduler stop ⇒ dead-man |

## 3. Fixtures policy

- `tests/fixtures/<integration>/<name>.json` + `<name>.source` (URL or "captured from <tenant/project>, <date>, redacted by <who>").
- Real captures are redacted with a script (`build/redact-fixture.py`) that replaces subscription ids, hostnames, emails; the redaction map is committed so identity fields stay consistent across a Fired/Resolved pair.
- Provisional vendor-doc samples are prefixed `PROVISIONAL-`; a CI check fails the **release** pipeline (not PR) if any provisional fixture remains for an integration marked `active` in seed config.
- Every mapping version stores its samples and expected outputs; `Verify` snapshots assert them.

## 4. Property and fuzz tests

- Fingerprint: same components in any key order ⇒ same hash; any changed component ⇒ different hash (FsCheck).
- Delivery key: retry of identical body ⇒ same key; body with only `ignore_paths` changed ⇒ same key.
- Ordering: random interleavings of `firing/update/resolved` with versions ⇒ final state equals the state from sorted application.
- Ingest: random bytes, huge JSON, deeply nested JSON ⇒ 202 or 413, never 500; processing ⇒ mapping_failure, never crash.
- Cron: 10,000 random schedules × DST transition dates in Europe/Warsaw, America/New_York, Australia/Sydney ⇒ `expected_next` strictly increasing.

## 5. Load (k6)

Profiles derived from the baseline (C9); until then: 500 ev/min sustained 60 min, 3,000 ev/min burst 15 min, 50 concurrent UI users paging the queue. Assert spec §20.2: ingest p95 < 500 ms; visible-state p95 < 5 s; first notification attempt p95 < 30 s; queue p95 < 2 s with 50k open episodes seeded; scheduler lag < 60 s throughout. Record DB size growth and autovacuum behaviour on `ops.job`.

## 6. Security tests

- Token brute force: 404 timing indistinguishable (statistical test on 1,000 requests).
- Payload with `<script>` in summary ⇒ rendered as text in UI and card.
- Scope escape via crafted `labels.scope` ⇒ ignored (scope from integration only).
- `If-Match` omitted on mutation ⇒ 428.
- OpenAPI security schemes present on every non-health route (contract test).
- Local auth: 11th failed login within 15 min ⇒ 423 for user and for IP independently; unknown user and wrong password produce byte-identical 401 bodies and statistically indistinguishable timing; `must_change_password` blocks every `/api/v1/*` route except `/me` and `/auth/change-password`; revoked session ⇒ 401 on next request; PAT cannot exceed its owner's permissions; expired PAT ⇒ 401; bootstrap admin password never appears in logs (grep test on captured log output); CSRF token missing ⇒ 403 on mutations from a cookie session.

## 7. Release checklist (automated where possible)

All §22 scenarios green · no `PROVISIONAL-` fixtures for active integrations · axe zero serious · load profile passed within 30 days · migration dry-run against a staging snapshot · Helm upgrade + rollback rehearsed · dead-man URL configured and firing · fallback destination present for every active destination.
