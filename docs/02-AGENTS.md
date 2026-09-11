# AGENTS.md — Alert Hub

Operating brief for the coding agent. Read this first, then `09-implementation-plan.md`, then the document for the milestone you are on. The specification (`spec/alert-hub-spec-v0.4.md`) is the source of truth for *behaviour*; this pack is the source of truth for *how it is built*. If the two conflict, stop and report the conflict — do not pick one.

## 1. What you are building

Alert Hub: a .NET backend and React frontend that ingests alerts from Azure Monitor, MongoDB Atlas and generic webhooks, deduplicates them into episodes, assigns ownership, auto-resolves on Hub-configured policies, verifies monitoring coverage with heartbeats and canaries, and delivers notifications through templated, signed outbound webhooks (Teams via Workflows, Slack, ticketing, automation platforms) and SMTP email. Single deployable modular application, PostgreSQL only, runs on AKS.

## 2. Stack (decided)

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 LTS, C# 14 | `net10.0` target everywhere |
| Web | ASP.NET Core minimal APIs | No MVC controllers |
| Data | EF Core 10 + Npgsql | Raw SQL via Dapper allowed only in the queue claim path and hot read models |
| DB | PostgreSQL 16+ | Single database; schemas `alert`, `hb`, `ops`, `audit` |
| Background | `BackgroundService` workers in a separate host project | Same solution, separate container image |
| Scheduling | DB-backed job table (see 05 §3) | No Hangfire/Quartz |
| Auth (users) | **Local username + password** (Argon2id) behind an `IIdentityProvider` abstraction; OIDC/Entra ID is a later provider, not a v1 dependency | Cookie session for UI; personal access tokens (hashed) for API clients and config-as-code |
| Auth (ingestion) | Per-integration bearer token, hashed at rest | Plus Atlas HMAC signature verification (algorithm configurable, see 07 §3) |
| Realtime | Server-Sent Events | No SignalR |
| Logging | Serilog → console JSON | OpenTelemetry for traces and metrics |
| Testing | xUnit, FluentAssertions, Testcontainers (Postgres), Verify for snapshots | See 10 |
| Frontend | React 19, TypeScript 5 strict, Vite | TanStack Query + Router, Tailwind v4, Radix primitives, design direction in 08 §0. Build the UI to that direction; do not wait for a company design system |
| Frontend testing | Vitest, Testing Library, Playwright | axe-core in Playwright for a11y |
| Packaging | Multi-stage Dockerfiles, Helm chart | Migrations run as a Helm pre-upgrade Job |

## 3. Repository layout

```
alert-hub/
├── AGENTS.md                      ← this file, copied to repo root
├── docs/                          ← this handoff pack + ADRs
├── src/
│   ├── AlertHub.Domain/           ← entities, value objects, state machines, fingerprinting. No EF, no HTTP.
│   ├── AlertHub.Application/      ← use cases, policies, mapping engine, routing engine, ports (interfaces)
│   ├── AlertHub.Infrastructure/   ← EF Core, Npgsql, outbox, job store, notification adapters, identity providers (local now, OIDC later)
│   ├── AlertHub.Api/              ← application API + SSE host
│   ├── AlertHub.Ingest/           ← public ingestion + heartbeat ping host (separate image, minimal deps)
│   ├── AlertHub.Workers/          ← processing, scheduler, dispatcher hosts
│   └── AlertHub.Contracts/        ← DTOs shared with the frontend generator (OpenAPI source)
├── web/                           ← React app
├── tests/
│   ├── AlertHub.Domain.Tests/
│   ├── AlertHub.Application.Tests/
│   ├── AlertHub.Integration.Tests/   ← Testcontainers; every acceptance scenario lives here
│   ├── AlertHub.Contract.Tests/      ← OpenAPI ↔ implementation drift
│   └── fixtures/                     ← real captured payloads only (see §6)
├── deploy/helm/alert-hub/
└── build/                         ← Dockerfiles, CI scripts
```

Dependency direction: `Domain ← Application ← Infrastructure ← hosts`. Domain has zero package references beyond the BCL.

## 4. Commands

```bash
# backend
dotnet build
dotnet test                                   # unit + integration (Docker required)
dotnet test --filter Category=Unit            # fast path
dotnet run --project src/AlertHub.Api
dotnet ef migrations add <Name> -p src/AlertHub.Infrastructure -s src/AlertHub.Api

# frontend
cd web && pnpm install && pnpm dev
pnpm test && pnpm e2e && pnpm typecheck && pnpm lint
pnpm generate:api                             # regenerate client from OpenAPI

# local stack
docker compose up -d postgres mailpit   # seeds an admin user; password printed once
```

## 5. Rules

1. **Behaviour comes from the spec.** Every acceptance scenario in spec §22 becomes at least one integration test with the scenario name in the test name. A milestone is not done until its scenarios pass.
2. **Invariants live in the database.** Uniqueness of `(integration_id, fingerprint)` for open episodes, uniqueness of applied `(integration_id, delivery_key)`, and episode version checks are constraints or conditional updates — never only application code. See 05 §2.
3. **Never acknowledge an event you have not committed.** Ingestion returns 2xx only after the raw event and its processing job are in the same committed transaction.
4. **Every state transition is audited** and every transition that notifies writes its outbox row in the same transaction. If you find yourself sending a notification outside a transaction boundary, stop.
5. **UTC in storage and APIs.** `timestamptz` only. Inject `TimeProvider`; never call `DateTime.UtcNow` in Domain or Application.
6. **Tokens and passwords are hashed at rest** (Argon2id) and tokens are shown in plaintext exactly once, on creation or rotation. Never log a password, token, or session id.
7. **No new packages without a one-line justification** in the PR description. No packages in Domain.
8. **Alert content is untrusted input.** Summaries, resource names and labels from payloads are rendered as text, never as HTML or Markdown, in both UI and notifications.
9. **Do not invent fixtures.** Payload samples in `tests/fixtures/` must be real captures or come from vendor documentation with the source URL in a sibling `.source` file. Mark provisional ones `PROVISIONAL-` in the filename.
10. **Do not implement anything from spec §5.2 (out of scope)** even if it seems small.
11. **When the spec is silent**, choose the most conservative behaviour (do not close, do not notify, route to triage), implement it, and list it under "Decisions made" in the PR description.
12. **Frontend consumes only the generated client.** No hand-written fetch calls.
13. **Migrations are forward-only** in `main`. Squash only in feature branches.
14. **One PR per vertical slice** (see 09). Each PR: passing tests, updated OpenAPI, updated ADR if a decision changed, `CHANGELOG.md` entry.

## 6. Definition of done — per milestone

- All mapped acceptance scenarios pass in `AlertHub.Integration.Tests`.
- `dotnet test` and `pnpm test && pnpm e2e` green in CI.
- OpenAPI regenerated and contract tests pass.
- No `TODO` without an issue reference.
- Helm chart deploys to a local kind cluster and `/healthz/ready` returns 200.
- README for the milestone's feature updated in `docs/`.

## 7. Things that look optional but are not

- SSE reconnect with `Last-Event-ID` — the "UI loses live connection" acceptance case depends on it.
- The external dead-man's switch for the Hub's own scheduler (spec §13.7) — a Helm value for the URL and a job that pings it every minute. Ship it in Milestone 5, not "later".
- Partitioned raw-event table from the first migration. Retrofitting partitioning is a data migration.
- `Idempotency-Key` on every mutating operator endpoint.

## 8. Where to look

| Need | Document |
|---|---|
| Why a decision was made | 03 |
| Exact state machine, dedup algorithm, timers | 04 |
| Tables, constraints, indexes, jobs | 05 |
| Endpoints, auth, payloads | 06 |
| Mapping / routing DSL and reference mappings | 07 |
| Screens, routes, components | 08 |
| Order of work | 09 |
| What to test and how | 10 |
