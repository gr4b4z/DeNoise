# Milestone 13 — Flap dampening (anti-flap)

Status: **planned**. Prerequisite: M5 (lifecycle policies, evidence model), M9 (grouping/suppression muting machinery).

## Problem

A condition that toggles `firing → resolved → firing` produces **one episode per cycle**. `EventProcessor.OpenAsync`
(`src/DeNoise.Application/Processing/EventProcessor.cs:263`) always calls `Episode.Open(..., latestClosed?.EpisodeId)`, so a
flapping source yields a chain of episodes linked by `previous_episode_id`, and `NotificationTransitionHook` stages an
`episode.opened` row for every one of them. The only dampening in the product today is `recovery_successes_required`, which
exists for **coverage and heartbeats only** (spec §13, scenario *Coverage alert flaps*). Regular alerts have no equivalent.

This is the single largest remaining noise source and it works directly against G2 (*notifications per distinct condition*).

## Spec basis — merged

**§11.4 "Flapping and reopening"**, **§12.6 "Closure hold"** and the five §22 rows below are in the specification; §11.3
(recurrence) is now scoped to firings outside the reopen window, which resolves the conflict between the two. Build against
those sections — they are the behavioural source of truth (AGENTS.md §5.1):

- **§11.4** — the reopen window, what a reopen is (not a new episode), which closures may be reopened, acknowledgement survival.
- **§12.6** — a closure notification may be held for a bounded period when the condition has recently flapped; the closure
  itself is never delayed, only the notification.
- **§22 rows (merged):**

| Scenario | Required result |
|---|---|
| Condition flaps within the reopen window | One episode with a reopen count; one open notification, not one per cycle |
| Flapping condition is acknowledged, then re-fires | Acknowledgement and assignee survive the reopen; no re-notification unless the policy asks |
| Flapping stops | Closure emitted after the hold with the normal evidence; a re-fire inside the hold cancels it |
| Human closed the episode, source re-fires | A **new** episode — a human closure is never rejoined |
| Reopen window expires before the next firing | New episode linked by `previous_episode_id`, as today |

## Policy surface

Flap settings belong to the existing **`lifecycle`** policy kind — it already resolves per routing rule → integration defaults →
mapping hint → type preset (`LifecycleService.ResolveAsync`), which is exactly the precedence flap needs. No new policy kind,
so config-as-code, impact preview, rollback and the policy editor come for free.

```yaml
lifecycle:
  profiles:
    explicit_recovery:
      flap:
        reopen_window: 10m        # a firing within this long after closure rejoins the closed episode
        rejoin_closures: [source, api_verification, heartbeat_and_inactivity, inactivity_unverified]
        notify_on_reopen: first   # never | first | every   (default: first)
        threshold: 3              # reopens inside the window before the episode is labelled flapping
        close_hold: 5m            # hold the closure notification once labelled flapping (0 = off)
        hold_max: 30m             # absolute cap on held closure notifications
```

Defaults are conservative per AGENTS.md §5.11: `notify_on_reopen: first`, `close_hold: 0` for profiles other than
`explicit_recovery`. Never-rejoined closures: `human`, `admin_expiry`, `expired_unverified`.

## Data model — migration `FlapDampening`

`alert.episode`:

| Column | Type | Purpose |
|---|---|---|
| `reopen_count` | `int not null default 0` | Reopens inside the current flap window |
| `flap_window_started_at` | `timestamptz null` | First reopen of the current window |
| `is_flapping` | `boolean not null default false` | Set when `reopen_count >= threshold`; cleared on a clean close |
| `closure_hold_until` | `timestamptz null` | Closure notification held until this time |

New job kind `flap_hold` in `src/DeNoise.Domain/Ops/JobKinds.cs`. Index: partial on `(closure_hold_until)` where not null.
The partial unique index on open `(integration_id, fingerprint)` is unchanged — a reopen clears `closed_at` inside the same
transaction that re-opens the condition, so the invariant still holds in the database (AGENTS.md §5.2).

## Engine changes

1. **`Episode.Reopen(evt, now, policy)`** (`src/DeNoise.Domain/Episodes/Episode.cs`) — condition `firing`, `closed_at`/
   `closure_reason`/evidence cleared into the timeline, `occurrence_count` and `reopen_count` incremented, `handling_state`
   **preserved** (an acknowledged episode stays acknowledged), `version` bumped.
2. **`EventProcessor.OpenAsync`** — `latestClosed` is already loaded there. When it is inside `reopen_window` and its closure
   reason is in `rejoin_closures`, take the same `FOR UPDATE` lock and reopen instead of opening. Timeline `episode.reopened`,
   audit `episode.reopen` with the reason and the window.
3. **`NotificationTransitionHook`** — a new `EpisodeTransitionKind.Reopened` stages `episode.opened` only per
   `notify_on_reopen`; suppression and grouping still run so a reopened child of a muted scope stays muted.
4. **Closure hold** — on close, if `is_flapping` and `close_hold > 0`, write `closure_hold_until` and a `flap_hold` job instead
   of the outbox row. The job stages the closure notification when it fires; a reopen inside the hold **cancels** the job and
   records `flap.hold_cancelled`. `hold_max` bounds the chain.
5. **Timers** — `auto_resolve` recompute already runs on every applied event; a reopen reschedules it through the existing path.

## API, UI, observability

- `EpisodeDetail` and `EpisodeListItem` gain `reopenCount`, `flapping`, `closureHoldUntil`; queue filter `flapping=true`;
  OpenAPI regenerated, contract tests updated.
- Queue: a *Flapping ×N* badge on the row; episode detail states the reopen history in the timeline and the held closure with a
  countdown. Playwright: flap badge appears, the timeline shows reopens.
- Metrics: `denoise_episode_reopens_total{integration}`, `denoise_flapping_open` gauge,
  `denoise_notifications_suppressed_total{reason="flap"}` — the last one is the first direct read on G2.

## Tests

`tests/DeNoise.Integration.Tests/Milestone13/FlapScenarios.cs` — one test per §22 row above, each named after the scenario.
Unit: `Episode.Reopen` state transitions, policy parsing and precedence, hold cancellation arithmetic (`Application.Tests`).

## Risks

- **A real new occurrence hidden as a reopen.** Mitigated by: `occurrence_count` still increments, every reopen is a timeline
  entry and an audit row, and the reopen window is bounded and per-profile.
- **Held closure looks like an open alert.** The UI states the hold explicitly; the condition state is already `resolved`.

## Definition of done

Spec amendment merged; all five scenarios green; OpenAPI and contract tests updated; `CHANGELOG.md` entry; policy editor and
config-as-code round-trip the `flap` block; Helm unchanged.

## Effort

Backend ~3 days, UI ~1 day, tests ~1 day.
