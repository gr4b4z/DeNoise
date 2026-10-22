# Milestone 15 — Adaptive grouping windows, late join, retro-adoption, digests

Status: **done** (backend, API, group window state in the episode detail, grouping impact preview). Prerequisite: M9 (grouping), M14 (so dependency wins over sibling grouping where both apply).

## What exists

| Area | Where | Notes |
|---|---|---|
| Policy | `GroupingPolicyDocument` (`GroupWindow`, `GroupDigest`, `GroupingRule.LookBack`, `MinMembers`) | `window: 10m` (scalar — unchanged behaviour: initial only, no extension, no grace, no retro) or `window: { initial, extend_by, max, late_join }` with `max` **mandatory whenever `extend_by` is present** and ≥ `initial`; `retro: { look_back }`; `min_members` (1–100, default 1); `notify: first_only \| first_and_new_critical \| digest`; `digest: { every, max_items }` required with and only with `notify: digest`. Every bound is validated with a JSON path. |
| Data | migration `AdaptiveGrouping`: `window_hard_end_at`, `last_member_at` (existing rows back-filled from `window_ends_at` / `opened_at`), `extension_count`, `adopted_count`, `digest_next_at`, `digest_last_at`, `continued_from_group_id`; `alert_group_digest_idx` | `AlertGroup.Accepts(at, lateJoin)` and `AlertGroup.Extend(by)` hold the window arithmetic. |
| Engine | `GroupingService.ApplyAsync` | **Join**: an open group for (rule, key) that still accepts members (`window_ends_at + late_join`) takes the episode, moves `last_member_at`, extends the end by `extend_by` capped at the hard end (`extension_count++`) and moves the pending `group_window_close` job with it. **Successor**: a group past its grace is closed in-line (so the one-open-group-per-key index admits the successor) and the new group records `continued_from_group_id`; a predecessor the timer closed is continued when the next member arrives within one initial window of that close. **Materialise**: a new group adopts open episodes of the same rule and key first seen inside `retro.look_back` (or inside the initial window when `min_members` > 1), `adopted_count` per member, timeline `grouped` with `adopted: true`; exactly one opening notification stands for the key — a dispatched one is never retracted, otherwise the earliest pending one is kept — and the other undispatched ones are coalesced; the new member notifies only when nothing stands yet (or as a new critical child under `first_and_new_critical`). **min_members**: below it no group exists and the episode notifies at once (timeline `belowMinMembers`); the group materialises on the next member and adopts the first. |
| Jobs | `group_window_close` (re-arms itself when the end moved after it was queued; closes hard at `max` even while members arrive), `group_digest` (`GroupDigestJobHandler`) | Digest: one `episode.group_digest` per team with the members that joined since the previous digest (first digest: everything since the group opened), capped at `max_items` with a `more` count, timeline `notify` on each listed member; re-armed every `every` while the group is open, one last time after it closed; shadow-integration members never reach the outbox. New built-in template `group-digest` (JSON; clone-only like the others). |
| API | `AlertGroupDto` gains `hardEndAt`, `lastMemberAt`, `extensionCount`, `adoptedCount`, `digestNextAt`, `digestLastAt`, `continuedFromGroupId`, `adoptedEpisodeIds`; `POST /policies/grouping/{id}/{v}/impact` | Preview: of the groups opened in the last 24 h, how many consecutive groups of the same key the proposed windows would have merged. |
| UI (`web/`) | Episode detail group section: *N episodes · window extended 4× · 2 adopted · window closes in 6 min (at the latest …) · next digest … · continues group …*; policy editor hint and impact preview for grouping. | |

## Acceptance

| Scenario | Test |
|---|---|
| A group member arrives just after the window closed | `Milestone15/AdaptiveGroupingScenarios.Scenario_AGroupMemberArrivesJustAfterTheWindowClosed_*` |
| Group members keep arriving | `Scenario_GroupMembersKeepArriving_*` (extension, cap, hard close by the timer, continuation) |
| A group forms after episodes of its key are already open | `Scenario_AGroupFormsAfterEpisodesOfItsKeyAreAlreadyOpen_*` (sent kept, pending coalesced, no duplicate, outside look-back untouched) |
| Group digest configured | `Scenario_GroupDigestConfigured_*` (cap, `more`, only new joins next time, no empty digest) |
| A lone episode never becomes a group | `Scenario_ALoneEpisodeNeverBecomesAGroup_*` |
| Scalar window regression | `Scalar_window_keeps_the_fixed_window_behaviour`, `Milestone9/GroupingScenarios` unchanged |
| Unit | `Application.Tests/Grouping/GroupingPolicyTests` |

## Decisions made

- **One opening notification per key, never a retraction.** Adoption keeps a notification that was already dispatched; otherwise the earliest pending one stands and the rest, including the new member's, are not staged. This is how "no duplicate notification" and "nothing already sent is retracted" hold at the same time.
- **A group under `min_members` leaves a trace** (`grouped` timeline entry with `belowMinMembers`), so the UI can say why a lone alert has no group.
- **A stale open group is closed in-line** by the successor's transaction instead of waiting for its close job: the partial unique index on open (rule, key) would otherwise refuse the successor. This also fixes the M9 race where a member arriving between window end and the close job's run conflicted.
- **Continuation** is recorded when the predecessor was closed in the same transaction (grace or maximum passed) or when the timer closed it less than one initial window before the next member; later members start an unrelated group.
- **The first digest lists every member** that joined since the group opened (the first member's own notification already went out); later digests list only what joined since the previous one; a quiet interval produces no digest.
- **Metrics** (`denoise_group_window_extensions_total`, `denoise_group_digests_total`, …) wait for the Application-layer metrics port noted in M13 and M14; `extension_count`, `adopted_count` and the digest timestamps on the group row give the read.

## Original plan

Prerequisite: M9 (grouping), M14 (so dependency wins over sibling grouping where both apply).

## Problem

Grouping today decides **once, at open time, against a fixed window**:

- `GroupingService.ApplyAsync` is called only from the open path of `NotificationTransitionHook`; afterwards only
  `OnSeverityRaisedAsync` touches the group (max severity).
- `alert.alert_group.window_ends_at` is computed from the first member and never moves (`GroupingPolicyDocument.DefaultWindow`
  = 10 min), so an episode arriving one second after the window starts a **second group** for the same key.
- An episode that opened just before the group formed is never adopted — the group only ever looks forward.
- `notify: first_only | first_and_new_critical` are the only outputs: after the first notification the group is silent for its
  whole life, with no periodic roll-up.

A storm that spans a few minutes therefore still produces several groups and several first-member notifications.

## Spec basis — merged

- **§16.3 "Grouping and suppression"** now carries the four window parameters (initial, extension, mandatory maximum,
  late-join grace), retroactive adoption, the minimum member count and the digest output.
- **§22 rows (merged):**

| Scenario | Required result |
|---|---|
| A member arrives just after the window closed | Joins the existing group through the grace period; no second group for the same key |
| Members keep arriving | The window extends up to `max`, then closes hard; the next episode starts a new group linked by `continued_from` |
| A group forms after episodes are already open | Recent open episodes are adopted; no duplicate notification; adoption is in the timeline |
| Digest enabled | One digest per interval with at most `max_items`, listing what joined since the last one |
| A lone episode never becomes a group | Notified immediately — grouping adds no latency below `min_members` |

## Policy surface — extends the existing `grouping` kind

```yaml
grouping:
  rules:
    - name: storm on one cluster
      match: { eq: [environment, production] }
      key: [resource_id, rule_id]
      window:
        initial: 10m
        extend_by: 5m          # each new member pushes the end out by this much
        max: 1h                # hard cap measured from the first member (mandatory)
        late_join: 2m          # grace after the end during which a member still joins
      retro:
        look_back: 5m          # open episodes opened this long before the group formed are adopted
      min_members: 2           # below this, no group exists and nothing is delayed
      notify: first_only       # first_only | first_and_new_critical | digest
      digest:
        every: 5m
        max_items: 20
```

Backward compatible: a scalar `window: 10m` keeps today's behaviour (`initial` only, no extension, no grace, no retro).
`max` is required whenever `extend_by` is present — an unbounded group is a dropped alert.

## Data model — migration `AdaptiveGrouping`

`alert.alert_group`:

| Column | Type | Purpose |
|---|---|---|
| `window_hard_end_at` | `timestamptz not null` | First member + `max`; the extension can never pass it |
| `last_member_at` | `timestamptz not null` | Drives the next extension |
| `extension_count` | `int not null default 0` | Observability and the UI explanation |
| `digest_next_at` | `timestamptz null` | Next digest due |
| `continued_from_group_id` | `uuid null` | Set when a group starts because its predecessor hit `max` |
| `adopted_count` | `int not null default 0` | Members taken in retroactively |

New job kind `group_digest`. The existing `group_window_close` job is **rescheduled** rather than executed when
`last_member_at` moved after it was queued — the reservation-token job model (M1) already supports re-queueing.

## Engine changes

1. **`GroupingService.ApplyAsync`** — window arithmetic moves into the group: a member inside `window_ends_at + late_join`
   joins and extends (`min(window_ends_at + extend_by, window_hard_end_at)`); the `group_window_close` job is re-armed to the
   new end. Extension is recorded on the group, not only in the log, so the UI can say *window extended 4×*.
2. **Retro-adoption** — when a group forms, query open episodes with the same `(rule_id, key_values)` opened within
   `retro.look_back`, set their `group_id`, write a `group.adopted` timeline entry each, and cancel their undispatched
   `episode.opened` rows through the existing relevance check. Dispatched notifications are never retracted — stated in the
   spec amendment and surfaced in the group view.
3. **`min_members`** — the first episode of a key notifies immediately and carries no group; the group is materialised when the
   second member arrives, adopting the first. This keeps grouping from adding latency to a single alert (protects G5/MTTA).
4. **Digest** — `group_digest` job stages one `episode.group_digest` notification per team destination with the members added
   since the last digest, capped at `max_items` with an *and N more* count; requires a new built-in webhook template
   (`group-digest`, M7 pattern) plus a `plain-text` variant for SMTP.
5. **Hard close** — at `window_hard_end_at` the group closes even if members are still arriving; the next member opens a new
   group with `continued_from_group_id` set, so a storm is a chain of bounded groups, never one unbounded group.

## API, UI, observability

- `GET /api/v1/groups/{id}` returns the window state (`initialEndsAt`, `currentEndsAt`, `hardEndAt`, `extensionCount`,
  `adoptedCount`, `continuedFrom`) and the member list with the adoption marker.
- Queue: the group row explains itself — *12 episodes · window extended 4× · closes in 6 min*; episode detail links to the group
  and states whether this episode was adopted.
- Policy editor: the YAML editor validates the new block; impact preview answers *how many of the last 24 h of groups would this
  rule have merged*.
- Metrics: `denoise_group_window_extensions_total`, `denoise_group_members` histogram, `denoise_group_digests_total`,
  and `denoise_notifications_suppressed_total{reason="grouping"}` — with M13 and M14 this completes the G2 read.

## Tests

`tests/DeNoise.Integration.Tests/Milestone15/AdaptiveGroupingScenarios.cs` — one test per §22 row. Unit: window arithmetic at
the `late_join` and `max` boundaries, retro-adoption selection, digest batching and the `max_items` cap, backward compatibility
of the scalar `window` form (`Application.Tests`).

## Risks

- **Unbounded notification delay.** `max` is mandatory and enforced in the parser; `min_members: 2` keeps single alerts instant.
- **Retro-adoption rewriting history.** Adoption is additive: a timeline entry per adopted episode, a counter on the group, and
  no retraction of anything already sent.
- **Storm fragmentation.** The `continued_from_group_id` chain keeps a long storm readable as one thread in the UI.

## Definition of done

Spec amendment merged; all five scenarios green; scalar-window policies unchanged in behaviour (regression test); `group-digest`
template seeded and clone-only like the other built-ins; OpenAPI and contract tests updated; `CHANGELOG.md` entry.

## Effort

Backend ~4 days, UI ~2 days, tests ~2 days.
