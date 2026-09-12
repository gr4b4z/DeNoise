# Milestone 4b — React shell, auth, work queue, episode detail, FreshnessBar

Status: **done** for the 09 milestone-4 UI scope (shell, auth, queue, detail overview + timeline, FreshnessBar). Later screens (teams, heartbeats, integrations, policies, hub health, admin) arrive with their backend milestones.

## What exists (`web/`)

| Area | Where | Notes |
|---|---|---|
| Toolchain | `package.json`, `vite.config.ts`, `tsconfig*.json`, `eslint.config.js` | React 19, TypeScript 5 strict (`noUncheckedIndexedAccess`), Vite, Tailwind v4 (`@tailwindcss/vite`), Radix primitives, TanStack Query + Router + Virtual, react-i18next (`en` only). `pnpm typecheck / lint / test / e2e / build / generate:api`. |
| Design tokens | `src/tokens.css`, `src/index.css` | The 08 §0 table verbatim for light and dark (`data-theme` override, OS default), `--sev-unknown` striped, density via `--row-height` (36/44 px), IBM Plex Sans with tabular numerals, radius ≤ 6 px, shadows only on drawer and menus, reduced-motion kills the pulse/collapse animations. |
| Generated client | `src/api/schema.d.ts` (openapi-typescript), `src/api/client.ts` (openapi-fetch) | The only transport (AGENTS.md rule 12; ESLint bans `fetch`). Middleware adds `X-CSRF-Token` to cookie mutations (token fetched once from `/auth/csrf`), announces 401s, and drops a stale token on a CSRF 403. `actionHeaders()` gives `If-Match` + `Idempotency-Key`; `withIdempotentRetry()` retries **network** failures twice with the same key (08 §5). |
| Auth | `src/auth/*`, `src/routes/Login.tsx`, `ChangePassword.tsx`, `src/app/router.tsx` | `/api/v1/me` loaded once in the shell route; 401 → `/login?returnTo=`; `mustChangePassword` → `/login/change-password`; `can(me, permission, scope)` mirrors 04 §8 (platform_admin sees every scope). Login copy per 08 §3.8 (`Incorrect username or password`, `Account locked — try again in N minutes`). SSO button appears when `/auth/providers` reports OIDC. |
| Shell | `src/app/Shell.tsx` | 240 px rail: views with counts (`/episodes/counts`, refreshed on SSE), teams, theme/density selectors, sign out; work surface; `FreshnessBar` footer. |
| Work queue | `src/routes/Queue.tsx`, `queueSearch.ts` | State in the URL (`view`, `severity[]`, `team`, `environment`, `service`, `q`, `episode`); severity chips; text search; virtualised table (`@tanstack/react-virtual`, 36 px rows) with severity left border, dashed border for `unknown` condition, dotted underline for suppressed, separate condition/handling badges, owner/assignee, age, last seen, indicator column (ack overdue, delivery failure, silenced, stale, routing correction, coverage), one primary action per row from `primaryAction()`; infinite keyset paging; `j/k/a/o//` keys; empty states `healthy` / `coverageWarning` / `filtered`. |
| Detail | `src/routes/Episode.tsx` | Shared by the 560 px drawer (`?episode=`) and `/episodes/:id`. Header with badges, owner, ack deadline, primary action and overflow (assign to me, note, silence 1 h, close, restore); Overview tab: `ExplanationPanel` (server `explanation` + routing `why`), identity components (mono), lifecycle + timers, routing, links, closure block with `EvidenceBadge`, coverage; Timeline tab: newest first, kind filter, late/replayed entries de-emphasised with a tooltip. Mutations send `If-Match`; 409 shows "Updated by someone else just now — refreshing" and takes the server's `current`. |
| Live updates | `src/realtime/sse.ts`, `cache.ts`, `RealtimeProvider.tsx`, `FreshnessBar.tsx` | One `EventSource` per tab; `episode.changed` patches every list cache in place when newer (version compare) and invalidates detail/counts; `resync` invalidates all; `heartbeat` keeps the bar live; >60 s silence ⇒ amber *stale*; failures ⇒ red *disconnected*; three failures ⇒ 30 s polling while probing the stream; reconnect after a failure refreshes the lists; the last event id survives a reload (`sessionStorage`) so the server can replay. Rows changed by SSE pulse for 600 ms. |
| Tests | `src/**/*.test.ts(x)`, `e2e/` | Vitest: `can()`, cache patching, `RealtimeClient` state machine (fake EventSource + fake timers), badges. Playwright + axe: login and queue with zero serious/critical violations; drawer shows explanation and timeline; **UI loses its live connection**. |
| CI | `.github/workflows/ci.yml` | `web` job (generated client current, typecheck, lint, unit, build) and `e2e` job (real API/Ingest/Workers on PostgreSQL + Playwright); backend job now fails on OpenAPI snapshot drift. |

## Acceptance scenario (spec §22)

| Scenario | Test |
|---|---|
| UI loses its live connection | `e2e/live-connection.spec.ts` — the stream is blocked; the FreshnessBar turns red ("Data may be out of date"), the queue stays visible, *Acknowledge* still works; an alert fired meanwhile appears once the stream is unblocked and the bar is live again. |

## Backend changes made for the UI

- OpenAPI metadata on every endpoint (`Produces`, `Accepts`, `ProducesProblem`) and an `[AsParameters] EpisodeListQuery` record so the generated client has typed responses and query parameters. `JsonNumberHandling.Strict` so integers are `number`, not `number | string`.
- SSE heartbeats are a named `heartbeat` event (browsers hide comment lines from `EventSource`).

## Decisions made

- **Code-based TanStack Router** (not file-based) — a dozen routes do not need the generator, and the route tree stays greppable.
- **Search state cast at one boundary** (`useSearch({ strict: false })` in the queue) to avoid a type cycle between the router and the page that owns the search schema.
- **Reconnect refreshes lists** even though the server replays from `Last-Event-ID`: a reload or a long outage can lose the id, and a refetch is cheaper than a stale row.
- **Silence is a fixed 1 h from the UI for now**; longer silences (which need `integration_admin`) come with the suppressions screen (08 §3.7).
- **Bulk selection, saved filters and the raw-payload tab** are deferred to the screens that need them; the API for all three exists.

## Running

```
cd web && pnpm install && pnpm dev            # proxies /api and /auth to http://localhost:8080 (DENOISE_API_URL overrides)
pnpm test && pnpm typecheck && pnpm lint
pnpm generate:api                              # after UPDATE_OPENAPI=1 dotnet test tests/DeNoise.Contract.Tests

# e2e: API on 8080, Ingest on 8081, Workers on 8082 against one database, then
E2E_ADMIN_PASSWORD='<Auth:Local:BootstrapPassword>' pnpm e2e
```

Set `PLAYWRIGHT_CHROMIUM_PATH` to reuse a pre-installed Chromium instead of `playwright install`.
