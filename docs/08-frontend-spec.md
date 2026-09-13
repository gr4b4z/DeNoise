# 08 — Frontend Specification (React)

Stack per AGENTS.md §2. The owner asked for a modern UI and no company design system is imposed. Section 0 is the design brief; it is binding.

## 0. Design direction

**Subject:** an operations console for people deciding, under time pressure, what is broken and who owns it. The design's job is to make severity, ownership and freshness legible at a glance and to stay quiet everywhere else.

**The one memorable element:** the severity system. Severity is the only saturated colour in the product. Everything else — chrome, tables, cards, buttons — is tonal. When an operator glances at the screen, colour means exactly one thing.

**Tokens** (`web/src/tokens.css`, both themes; theme follows OS, user-switchable, persisted):

| Token | Light | Dark | Role |
|---|---|---|---|
| `--bg` | `#F7F8FA` | `#12151A` | page |
| `--surface` | `#FFFFFF` | `#1A1E25` | table, panels, drawer |
| `--surface-2` | `#EEF0F3` | `#232830` | rail, hover rows, inputs |
| `--ink` | `#161A20` | `#E8EAEE` | text |
| `--ink-2` | `#5B6472` | `#9AA3B0` | secondary text |
| `--line` | `#DDE1E6` | `#2E343E` | hairlines |
| `--accent` | `#2F6FEB` | `#6EA0FF` | interactive: links, focus ring, primary button |
| `--sev-critical` | `#C8102E` | `#FF5A6E` | |
| `--sev-high` | `#D9660D` | `#FF9A3C` | |
| `--sev-medium` | `#B58900` | `#E6C04A` | |
| `--sev-low` | `#2E7D32` | `#6BCB77` | |
| `--sev-info` | `#4A5568` | `#8B96A5` | tonal, deliberately un-alarming |
| `--sev-unknown` | striped `--sev-high`/`--surface` | same | unknown must look *unresolved*, not calm |

Coverage and freshness use `--sev-high` (degraded) and `--sev-critical` (unavailable / disconnected) — the same language, because they are the same kind of problem.

**Type:** IBM Plex Sans for everything, tabular numerals on (`font-variant-numeric: tabular-nums`) so ages, counts and timestamps align in columns. IBM Plex Mono only for identifiers that are copied or compared (fingerprint components, resource ids, ping URLs, YAML) — never for labels. Scale: 12 / 13 (table body) / 14 (UI body) / 16 / 20 / 28. Weights 400 and 600 only. Sentence case everywhere; no all-caps labels; no eyebrow labels above headings.

**Layout:** a fixed 240 px left rail (views with counts, then teams, then admin), a full-width work surface, and a **right-side detail drawer** (560 px, resizable) that opens an episode without leaving the queue — the primary triage loop is queue → drawer → action → next row, never a page navigation. Direct links (`/episodes/:id`) render the same content full-page. Density is high by default (row height 36 px) with a "comfortable" toggle (44 px). Left-aligned throughout; numbers right-aligned.

```
┌────────┬──────────────────────────────────────────────┬──────────────────────┐
│ rail   │ filters ▸ severity team env service   ⌕      │ drawer (episode)      │
│ views  ├──────────────────────────────────────────────┤ ● CRITICAL  firing    │
│  12 ▍  │ ● Orders API 5xx > 2%   prod  MPT   new  4m  │ Owner: MPT DevOps     │
│   3    │ ● Atlas HOST_DOWN       prod  PRSM  ack  18m │ [Acknowledge] [Assign]│
│  ...   │ ◌ Disk 85% eu-west      stag  CIQ   new  1h  │ explanation…          │
│ teams  │ …                                            │ identity · timeline   │
├────────┴──────────────────────────────────────────────┴──────────────────────┤
│ freshness: live · last event 3 s ago · 4 integrations healthy · 1 degraded    │
└──────────────────────────────────────────────────────────────────────────────┘
```

**Structure encodes information:** a left border on a row is the severity; a hairline separates groups; a dashed border marks `unknown` condition; a dotted-underline marks suppressed. Nothing has a border-radius larger than 6 px; nothing has a drop shadow except the drawer and menus. No gradients, no illustration, no decorative icons — icons only where they replace a word in a tight column.

**Motion:** only in response to change. A row updated by SSE gets a 600 ms background pulse in `--surface-2`; a row leaving the view (acknowledged, closed) collapses over 200 ms. No page-load animations, no hover lifts. `prefers-reduced-motion` disables both.

**Copy:** plain verbs, sentence case, the same verb across button → toast → timeline (`Acknowledge` → `Acknowledged` → `Acknowledged by …`). Errors state what happened and what to do; empty states name what is true ("Nothing needs attention. Coverage verified for 5 integrations.").

**What to avoid, explicitly:** the SaaS-card kit (identical rounded cards with soft shadows), cream backgrounds with warm-clay accents, mono for small labels, all-caps eyebrows, middle-dot meta strings, arrows appended to buttons, KPI tiles with giant numbers on the queue screen (counts live in the rail, small). The team overview may use compact stat blocks because there the numbers *are* the content.

## 1. Principles that translate to code

| Spec principle (§14.1) | Implementation rule |
|---|---|
| Next action clear | Every row and detail header has exactly one primary action button computed from `handlingState` + permissions |
| Condition ≠ handling | Two separate badges, never merged into one "status" |
| Show freshness | Global `FreshnessBar`: last SSE event time, connection state; stale (>60 s no event and no heartbeat comment) ⇒ amber, disconnected ⇒ red banner "Data may be out of date" |
| Expose uncertainty | `CoverageBadge` on every row whose integration is not `healthy`; `EvidenceBadge` on every closure |
| Explain automation | `ExplanationPanel` renders the server-built `explanation` string and `routing.why` |
| Triage without JSON | Raw payload behind a permission-gated tab, collapsed by default |

## 2. Routes

```
/                                   → redirect /queue?view=needsAttention
/queue                              work queue (view, filters, sort in URL)
/episodes/:id                       detail (tabs: overview, timeline, related, delivery, raw*)
/history                            closed episodes
/teams  /teams/:id                  team overview
/heartbeats  /heartbeats/new  /heartbeats/:id
/integrations  /integrations/new (wizard)  /integrations/:id (tabs: health, mappings, failures, coverage)
/policies/:kind  /policies/:kind/:id (versions, diff, impact, activate)
/suppressions
/destinations  /destinations/:id            webhook + email destinations, test send, deliveries
/templates  /templates/:id                  webhook body templates (versions, render preview)
/hub                                hub health (operator+)
/audit
/login  /login/change-password (forced)  /login/reset  /login/reset/:token  /logout  /403  /404
/admin/users  /admin/users/:id           user management (platform_admin)
/me/tokens  /me/sessions                 personal access tokens, active sessions
```
Every filterable screen serialises its state to the URL (shareable links, spec §14.3). Saved filters are stored server-side per user (`/api/v1/me/filters`) — add to 06 if not present.

## 3. Screens

### 3.1 Work queue (`/queue`)
- Left rail: views (Needs attention, My alerts, My teams, Unassigned, Acknowledged & active, Stale/unverified, Suppressed, Closed). Counts from `/episodes?view=…&limit=0&includeTotal=true` refreshed on SSE.
- Filter bar: severity (multi), environment (multi chips: All · Production · Staging · Development · Unknown, plus any value carried by the URL), team, scope, service, integration, text search. Sort: default server order (`-severityRank,ackOverdue,-lastSeen`); user may override.
- Default environment (spec §15.5, §14.3): every entry point — rail views, login redirect, "back to queue" — links to `?environment=production&environment=unknown`. A URL without `environment` means all environments; the default is applied by the links, never injected by the route, so shared URLs and saved filters mean exactly what they say.
- Table (virtualised, `@tanstack/react-virtual`): severity, summary, resource/service, env, condition badge, handling badge, owner/assignee, age, last evidence, indicators column (ack overdue ⏱, next escalation, auto-close at, suppression ⏸, delivery failure ⚠, stale, coverage).
- Row actions: primary (Acknowledge / Assign to me / Open), overflow (Assign…, Silence…, Close…).
- Bulk: checkbox selection ≤ 200 → sticky action bar showing "N selected"; confirmation modal lists the action and the exact count; results modal lists per-item failures.
- Empty state depends on coverage: all relevant integrations healthy ⇒ "Nothing needs attention — coverage verified"; otherwise ⇒ warning empty state naming degraded integrations.
- Keyboard: `j/k` move, `a` acknowledge, `o` open, `/` focus search, `?` help.

### 3.2 Episode detail (`/episodes/:id`)
Header: severity, summary, resource, condition + handling badges, owner/assignee, deadlines, primary action, overflow.
Tabs:
- **Overview:** explanation panel; identity components table ("why these events were combined"); lifecycle policy resolved values + timers (auto-close at, suspended reason); routing rule + why; links (source, runbook); closure block with evidence when closed; coverage state of integration.
- **Timeline:** merged `episode_event` + delivery attempts, newest first, filter by kind; late/replayed events visually de-emphasised with a tooltip explaining why they did not change state.
- **Related:** previous/next episodes for identity; group members with their own states.
- **Delivery:** outbox rows and attempts per destination; fallback used flag.
- **Raw:** permission-gated; per applied event; "payload no longer retained" state.
Concurrency: all mutations send `If-Match`; on 409 show inline "Updated by {actor} just now — refreshing" and refetch; never overwrite.

### 3.3 Team overview (`/teams/:id`)
Cards: unassigned, ack overdue, acknowledged > follow-up window, stale/unverified, coverage of team's integrations. Each card links to the queue with filters applied. Member list, destinations with fallback, coverage hours.

### 3.4 Heartbeats
List: name, state badge, schedule (human: "every 5 min", "daily 02:30 Europe/Warsaw"), last ping (relative), owner, bound integration, next expected.
Create/edit form: name, team, schedule kind toggle (interval / cron with tz picker and "next 5 runs" preview), grace, severity on miss, bind to integration, auto-pause. On create: **one-time modal** with ping URL + copyable snippets (`curl -fsS --retry 3 <url>`, PowerShell `Invoke-RestMethod`, GitHub Actions step, cron line). Detail: run history ring (kind, duration, exit code, body tail), pause/resume with reason, rotate token (confirm, then one-time modal).

### 3.5 Integrations
List with health badge, last processed alert, mapping failure rate, backlog age. Wizard (spec §14.5) as 10 steps with server-side validation per step and a final dry-run table. Mapping editor: YAML (CodeMirror 6) with schema validation, sample payload pane, live preview (calls `/preview`), diff against active version, activate button disabled until samples pass. Failures tab: quarantined events, error, field; actions: preview with new mapping version, retry, dismiss.

### 3.6 Policies
Per kind: version list, YAML editor with schema hints, diff, **impact preview** (lifecycle/routing: affected open episodes count + sample list) shown before the Activate button enables, rollback.

### 3.7 Suppressions
Calendar-ish list of maintenance windows and silences with scope predicate rendered human-readably, tz, actor, reason. Create form requires reason and end.

### 3.7b Destinations and templates
Destinations list: name, channel (webhook / email), team, subscribed event types, last success / failure, consecutive failures, fallback. Form: URL (masked after save; `Reveal` requires re-typing password), method, headers (key/value, values masked), template picker with **live render preview** against a real recent episode or the sample, signing secret (generate button, shown once), timeout, event-type checkboxes, fallback (required; cannot be self). `Send test` shows status, latency and response excerpt inline. Deliveries tab: attempts with outcome, status, latency, `used_fallback`, response excerpt.
Templates: list with built-in badge; editor (CodeMirror, Liquid highlighting) with the notification model shown as a collapsible reference on the right, render preview, validation errors inline, version diff, activate. Built-ins are cloneable, not editable.

### 3.8 Login and first run
`/login`: centred single column on `--bg`, product name set in Plex Sans 600 at 28 px, one form — username, password, "Keep me signed in", `Sign in`. When `/auth/providers` reports OIDC, a second button `Sign in with SoftwareOne SSO` appears above a hairline. Errors are inline and specific to what the server said: `Incorrect username or password` (never which), `Account locked — try again in 12 minutes`. No marketing copy, no illustration.
Forced change (`/login/change-password`): shown after login when `mustChangePassword`; explains why in one sentence; strength feedback inline; `Set new password` → straight to the return URL.
Reset (`/login/reset`): email field, always confirms "If that address exists, we sent a link"; hidden entirely when the server reports self-service reset disabled.
Admin user management (`/admin/users`): table (username, name, roles, scopes, provider, status, last login), create user (temporary password shown once in `OneTimeSecretDialog`), reset password, disable/enable, unlock, edit roles and scopes.
`/me/tokens`: create PAT (name, scopes ⊆ own, expiry) → one-time display; list with last used; revoke. `/me/sessions`: device, IP, last seen; revoke others.

### 3.9 Hub health (`/hub`)
Component heartbeats, queue depth/oldest age per job kind, outbox lag, failed queue counts, external dead-man status, DB connectivity. Failure queue with retry (platform_admin).

## 4. Shared components

`SeverityBadge`, `ConditionBadge`, `HandlingBadge`, `EvidenceBadge`, `CoverageBadge`, `FreshnessBar`, `RelativeTime` (with absolute UTC tooltip and user-locale local time), `Duration`, `PredicateView` (renders predicate YAML as sentence), `ExplanationPanel`, `ConfirmDialog` (reason field variant), `OneTimeSecretDialog`, `YamlEditor`, `VirtualTable`, `EmptyState` (variant: healthy | coverageWarning | filtered), `ProblemBanner` (renders Problem Details).

All badges: text + icon + colour; colour is never the only carrier (WCAG 1.4.1).

## 5. State and data

- Server state: TanStack Query. Query keys namespaced: `['episodes', filters]`, `['episode', id]`, `['heartbeats', filters]`, ….
- SSE client (`web/src/realtime/sse.ts`): single `EventSource` per tab with `Last-Event-ID`; on `episode.changed` ⇒ `invalidateQueries(['episode', id])` and patch list caches in place for the row (version compare — ignore if cache version ≥ event version); on `resync` ⇒ invalidate all; exponential backoff reconnect; fall back to 30 s polling after 3 failed reconnects, with FreshnessBar reflecting it.
- Auth: `/api/v1/me` on load; roles/scopes gate routes and buttons (`can(permission, scope)` helper mirroring 04 §8).
- Idempotency: mutations attach a fresh UUID `Idempotency-Key`; on network error the same key is retried up to 2×.
- Generated client from OpenAPI (`openapi-typescript` + `openapi-fetch`); hand-written fetch is a lint error.

## 6. Accessibility and quality gates

- Keyboard navigable end to end; visible focus; `aria-live="polite"` region for FreshnessBar and action results.
- Playwright + axe: zero serious/critical violations on every route.
- Responsive: queue collapses to card list under 768 px with the same primary action; detail tabs become an accordion.
- Performance: queue render < 100 ms for 500 rows (virtualised); initial load < 2 s on 3G-fast profile.
- i18n scaffolding (`react-i18next`) with `en` only; strings in `web/src/locales/en.json`.

## 7. Error and empty semantics

| Situation | UI |
|---|---|
| 401 | redirect to `/login` preserving return URL |
| 403 | in-place "no access to this scope" — never blank |
| 409 | inline refresh banner (see 3.2) |
| 429 | toast with retry-after countdown |
| SSE disconnected | red FreshnessBar; data still shown; mutations still allowed |
| Partial bulk failure | results modal, failed rows remain selected |
