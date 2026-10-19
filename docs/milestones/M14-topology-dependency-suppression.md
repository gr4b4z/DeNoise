# Milestone 14 — Topology and dependency suppression

Status: **done** (backend, API, policy kind in the editor, queue flags, *Depends on* / *Dependents* cards). Prerequisite: M13 (so a dependent child that flaps is already one episode), M9 (muting machinery).

## What exists

| Area | Where | Notes |
|---|---|---|
| Policy kind | `PolicyKinds.Topology`, `WellKnownPolicies.Topology` (single document), `Application/Topology/TopologyPolicy.cs` (`TopologyPolicyDocument`, `TopologyPolicyValidator`) | `topology.rules[]`: `parent { match \| static, key }`, `child { match, key }` (same length as the parent key, or empty for a static parent), `window` (default 15 m, ≤ 24 h, measured from the parent's opening), `look_back` (default 0), `action: mute_child \| group_under_parent \| annotate_only` (default `annotate_only` — observation mode first), `mute_severities` (default high/medium/low/informational: `critical` and `unknown` only when listed). Predicates and references reuse `PredicateParser` / `RoutingRefs`; errors carry JSON paths. **Cycle detection** (`PredicateImplication`): rule *i* leads to rule *j* when *i*'s child predicate structurally implies *j*'s parent predicate (identical predicates, `eq`/`in` inside a wider `in`, an `all` containing the parent's atoms, a parent matching everything); any cycle, a self-loop included, is refused with `$.topology.rules[i]` and the chain of rule names. The `cfg.policy` kind check constraint admits `topology`; config-as-code, versions, rollback and the editor need no new plumbing. |
| Data | migration `Topology`: `depends_on_episode_id` (FK, partial index), `dependency_rule_id`, `dependency_action`, `dependency_window_ends_at`, `dependency_released_at`; `episode_scope_first_seen_idx`; `suppression_source = 'topology'` (`SuppressionSources.Topology`) | The relation is on the child; `IsDependent` = attached and not yet released. |
| Engine | `Application/Topology/TopologyService.cs` | **Child side** (`ApplyAsync`, called by `NotificationTransitionHook` after suppression windows and before grouping, on open and on reopen): first rule whose child side matches *and* whose parent is open inside the window wins; parents are searched among open episodes of the same access scope first seen inside the window, newest first, judged by their latest normalised event (`rule_id`, `resource_id`, `dimensions.*`, `labels.*` live there). `mute_child` sets `suppressed_until = parent.first_seen + window` with source `topology`, so the existing staging writes the opening rows as `suppressed`; `group_under_parent` joins the parent's group or opens one keyed on the parent episode (parent = first member, child a quiet member unless critical and not muted); `annotate_only` records the relation. **Parent side** (`AdoptAsync`, after the parent's own staging): open episodes of the scope first seen inside `look_back` that match the child side are attached; their *pending* opening rows become `suppressed`, dispatched rows are untouched. **Release** (`ReleaseDependentsAsync`): on parent close (source, system *and* manual close, inside the closing transaction) and at window end (`dependency_release` job, one per parent and window end): `dependency_released_at` set, topology mutes cleared, suppressed rows coalesced (never replayed), one `episode.summary_after_dependency` per team with still-firing dependents (new notification type, in the default subscription). Every attach, adoption and release is a `dependency` timeline entry on child and parent. |
| API | `EpisodeListItem.dependsOnEpisodeId`, `dependencyReleased`, `dependents`; `GET /episodes?dependents=true\|false`; `GET /episodes/{id}/related` adds `dependsOn` and `dependents[]`; `POST /policies/topology/{id}/{v}/impact` | Impact preview: of the episodes opened in the last 24 h and still open, how many this version would have attributed and how many muted, with a sample. Explanation paragraph states the attribution, the mute and the roll-up. |
| UI (`web/`) | Policies: *Topology* kind with skeleton, hint, impact preview gating Activate; queue flags `⤷` (dependent) and `+N` (dependents rolled up); episode detail *Depends on* card (parent link, released state) and *Dependents* list. | Muted dependents already leave *Needs attention* through the existing suppression clause; they stay in every other view, in search and in history. |

## Acceptance

| Scenario | Test |
|---|---|
| Host goes down with its services | `Milestone14/TopologyScenarios.Scenario_HostGoesDownWithItsServices_*` |
| Child fires before its parent | `Scenario_ChildFiresBeforeItsParent_*` (adopted inside `look_back`, a sent row stays sent, outside the look-back not adopted) |
| Parent closes while a dependent still fires | `Scenario_ParentClosesWhileADependentStillFires_*`; window end: `Window_end_releases_dependents_of_a_parent_that_is_still_open` |
| No parent matches a dependency rule | `Scenario_NoParentMatchesADependencyRule_*` |
| Dependency policy declares a cycle | `Scenario_DependencyPolicyDeclaresACycle_*` and `Application.Tests/Topology/TopologyPolicyTests` |
| `critical` dependent under a non-critical parent | `Scenario_CriticalDependentUnderANonCriticalParent_*` |

## Decisions made

- **No `release:` knob.** Spec §16.5 releases dependents when the parent closes *or* the window ends; both happen, whichever comes first, and a `release` key is refused at validation so the document cannot promise something else.
- **The parent search is bounded**, not heuristic: open episodes of the same access scope first seen inside the window, newest first, at most 1 000 per lookup; the first that satisfies the parent predicate with equal key values wins. Rule order resolves which rule, recency resolves which parent.
- **Dependency is decided when the child opens (or reopens)**, never re-evaluated afterwards; a parent that opens later only adopts through `look_back`. Activating a new topology version therefore changes nothing for existing episodes, which the impact preview states.
- **Cycle detection is structural**, not a satisfiability check: it refuses the cycles a person can write (identical or nested equality predicates) and documents the rule. A chain (cluster → host → services) is allowed.
- **`unknown` severity is not muted by default**, like `critical`: what the mapping could not classify is not hidden behind a dependency unless the rule says so.
- **Metrics** (`denoise_notifications_suppressed_total{reason="dependency"}`, `denoise_dependent_open`) wait for an Application-layer metrics port (same note as M13); the `dependents` filter and the `dependency` timeline kind give the read meanwhile.

## Original plan

Prerequisite: M13 (so a dependent child that flaps is already one episode), M9 (muting machinery).

## Problem

One failing host produces one alert per service on it. DeNoise groups by **field equality inside a time window**
(`GroupingService`), which flattens siblings but knows nothing about *parent → child*: the host-down episode and the forty
service episodes are peers, and all forty notify. There is no relation on the episode expressing "this is a consequence of that".

## Scope boundary (decided up front)

**No CMDB, no discovered topology, no graph import.** Dependencies come from two places only:

1. **Field equality** between a child episode and an open parent episode (`child.host == parent.resource_id`).
2. **Static parents declared in the policy** for cases the payload cannot express.

Automatic topology discovery, service maps and dependency inference stay out of v1 — they are the slope towards the RCA that
spec §5.2 excludes.

## Spec basis — merged

- **§16.5 "Dependency suppression"** — the parent/child relation, the dependency window, what a muted child is (visible,
  owned, actionable; not notified), the rule that dependents are **never hidden from the UI, only from notification**, the
  observation mode, and the exclusion of discovered topology.
- **§22 rows (merged):**

| Scenario | Required result |
|---|---|
| Host goes down with its services | One notification for the parent; children visible under it, each still owned and actionable |
| Child fires before its parent | Children opened inside the look-back are adopted when the parent opens; already-sent notifications are not retracted |
| Parent closes while a child still fires | Children released; one summary per team; the child becomes a top-level actionable episode |
| No parent matches | The child notifies normally (conservative default, AGENTS.md §5.11) |
| Policy declares a dependency cycle | Refused at validation with the path to the offending rule |
| Critical child under a non-critical parent | Notifies on its own unless the rule explicitly mutes critical |

## Policy surface — new kind `topology`

New `PolicyKinds.Topology` (`src/DeNoise.Domain/Policies/PolicyVersion.cs`). Versioned like every other kind, so
`/api/v1/policies/topology`, impact preview, rollback, export/import and the policy editor need no new plumbing.

```yaml
topology:
  rules:
    - name: host down suppresses its services
      parent:
        match: { eq: [rule_id, "host-down"] }
        key: [resource_id]
      child:
        match: { ne: [rule_id, "host-down"] }
        key: [host]                      # child key values must equal the parent's key values
      window: 15m                        # children opening this long after the parent are dependent
      look_back: 2m                      # children opened this long *before* the parent are adopted
      action: mute_child                 # mute_child | group_under_parent | annotate_only
      mute_severities: [high, medium, low, informational]   # critical notifies unless listed
      release: on_parent_close           # on_parent_close | on_window_end
    - name: cluster down suppresses its nodes
      parent: { static: ["atlas-cluster-0"], key: [resource_id] }
      child:  { match: { eq: [dimensions.cluster, "atlas-cluster-0"] }, key: [] }
      window: 30m
      action: group_under_parent
```

Predicates and key references reuse `PredicateParser` and `RoutingRefs` — no new DSL machinery, and validation errors carry
JSON paths like the other kinds. Cycle detection runs on save: a rule whose child predicate can match its own parent predicate
with the same key is refused (`$.topology.rules[i]`).

## Data model — migration `Topology`

`alert.episode`:

| Column | Type | Purpose |
|---|---|---|
| `depends_on_episode_id` | `uuid null` → `alert.episode` | The parent; indexed |
| `dependency_rule_id` | `uuid null` | Which topology rule decided it |
| `dependency_released_at` | `timestamptz null` | Set when the parent closed or the window ended |

`suppression_source` (exists since M9) takes the new value `topology`, so the muted-row coalescing and the
`episode.summary_after_suppression` staging are reused rather than duplicated — the dependency summary is
`episode.summary_after_dependency` with the same shape.

New job kind `dependency_release` for `release: on_window_end`; the `on_parent_close` path runs inside the parent's closing
transaction (AGENTS.md §5.4 — outbox row in the same transaction).

## Engine changes

1. **`Application/Topology/TopologyService.cs`** — `ApplyAsync(episode, evt, integration, session, now, ct)` called from
   `NotificationTransitionHook` **after** `suppressions.ApplyToNewEpisodeAsync` and **before** `grouping.ApplyAsync`
   (`src/DeNoise.Application/Notifications/NotificationTransitionHook.cs:138`). It finds the first matching rule, then the open
   parent episode with equal key values inside `window`, sets the dependency columns, and returns the decision.
2. **Notification staging** — `mute_child` sets `suppressed_until` with `suppression_source = 'topology'` and skips the
   `episode.opened` row; `group_under_parent` hands the parent's `group_id` to `GroupingService` instead of computing a key;
   `annotate_only` only records the relation.
3. **Adoption (look-back)** — at parent open, open episodes matching the child side within `look_back` get the relation and, if
   their notification has not been dispatched yet, their outbox row is cancelled through the existing relevance check.
   Dispatched notifications are never retracted.
4. **Release** — on parent close (or window end), children with `dependency_released_at is null` are released; those still
   firing stage one `episode.summary_after_dependency` per team destination.

## API, UI, observability

- Episode DTO gains `dependsOn` and `dependents[]`; `GET /episodes/{id}/related`
  (`EpisodeQueries.RelatedAsync`) returns the dependency edges alongside `sameIdentity` and `groupMembers`.
- Queue: a parent row rolls up *+N dependent*; new filter `includeDependents` (the *Needs attention* view defaults to
  excluding released-less dependents — this is where the noise reduction is actually visible).
- Episode detail: a *Depends on* card with the parent, the rule name and the plain-language reason; children list on the parent.
- Policy editor gains the `topology` kind with the same YAML editor, diff, impact preview (*how many open episodes would this
  rule have muted in the last 24 h*) and rollback.
- Metrics: `denoise_notifications_suppressed_total{reason="dependency"}`, `denoise_dependent_open` gauge.

## Tests

`tests/DeNoise.Integration.Tests/Milestone14/TopologyScenarios.cs` — one test per §22 row. Unit tests: rule parsing, cycle
detection, key-equality matching, look-back adoption boundaries (`Application.Tests`).

## Risks

- **A genuine independent failure muted as a consequence.** Mitigations: `critical` is never muted unless explicitly listed;
  dependents stay fully visible, owned and actionable in the UI and API; every mute is a timeline entry; `annotate_only` lets a
  team run the rule in observation mode before muting anything.
- **Wrong parent chosen in a storm.** The first matching rule wins and the parent must be *open* with *equal keys* — no
  heuristics, no scoring. Ambiguity is resolved by rule order, which is visible in the policy.

## Definition of done

Spec amendment merged; all six scenarios green; `topology` round-trips through config export/import; impact preview implemented;
OpenAPI and contract tests updated; `CHANGELOG.md` entry.

## Effort

Backend ~5 days, UI ~2 days, tests ~2 days.
