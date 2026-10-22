# Milestone 2 — Normalise, identity, ordering, episodes

Status: **done**. No owner input required (09).

## What exists

| Area | Where | Notes |
|---|---|---|
| Mapping DSL | `Application/Mapping/*` | YAML → JSON (`YamlJson`), typed model (`MappingModel`), parser with per-path errors (`MappingParser`), interpreter (`MappingEngine`). All rule kinds from 07 §1: `path`, `const`, `first_nonempty` (+`when`), `lookup` (`map`/`regex_map`/`default`), `template`, `object`, `pick`, `array` (+`join`), `header`; modifiers `as`, `transform`, `default`, `max_length`, `when`; `required`, `identity`, `enrich` + `lookup_tables`, `ignore_paths_for_body_key`, `applies_when`, `lifecycle_profile_hint`. JSONPath via JsonPath.Net with the `$headers` pseudo-root. |
| Predicates | `PredicateParser`, `PredicateEvaluator` | 07 §5 grammar; severity refs compare on the canonical ordinal (`unknown` ranks as `high`). Reused by routing/grouping/suppression later. |
| Built-in mapping | `BuiltInMappings.GenericWebhook` | The 07 §4 producer contract; used automatically by `generic_webhook` integrations with no mapping. Version 0 marks it in `normalised_event.mapping_version`. |
| Mapping versions | `cfg.mapping`, `MappingService`, `EfMappingRepository`, `CachedMappingResolver` | Immutable versions, ★ one active per mapping id; create → inactive; activation re-runs stored samples and refuses on any mismatch; ordered evaluation with `applies_when`, first match wins; 15 s list cache with explicit invalidation on activation. |
| Delivery key | `Domain/Alerts/DeliveryKey` | 04 §3 strategies `evt:` → `ver:` → `ts:` → `body:` (canonical JSON minus ignore paths, SHA-256); `identity_confidence` `exact`/`body`. |
| Fingerprint | `Domain/Alerts/Fingerprint` | 04 §4 canonicalisation (NFC, trim, optional case folding, dimension keys sorted, `∅` for optional-missing, U+001F separators, prefixed by integration id + identity version), components persisted for explainability. Missing non-optional component ⇒ mapping exception, never a merge. |
| Ordering | `Domain/Episodes/Ordering` | 04 §5: producer versions (numeric when both parse) beat timestamps; otherwise `occurred_at ≥ last_seen − 120 s`; an older `resolved` is late; closed-before-open marker in `alert.source_instance_state`. |
| Episode aggregate | `Domain/Episodes/Episode` | Condition × handling dimensions, transitions of 04 §2 (open, signal/update with max severity, source resolved/cancelled, unknown ↔ firing, acknowledge, assign, manual close, system close, restore), `Version` as concurrency token. |
| Processing | `Application/Processing/EventProcessor`, `Infrastructure/Processing/*` | Load raw → select mapping → normalise → one transaction: ledger insert (`ON CONFLICT DO NOTHING`), open episode `FOR UPDATE`, ordering, transition, timeline, audit, `ITransitionHook` → commit → `IEpisodeChangePublisher`. Conflicts on `episode_one_open_per_identity` or the version token retry from scratch (max 5). Mapping failures are quarantined in `alert.mapping_failure` and the job completes. |
| Schema | migration `Episodes` | `alert.normalised_event`, `applied_event` ★, `delivery_duplicate`, `source_instance_state`, `identity`, `episode` (+ `search_tsv` generated column and GIN index), `episode_event`, `mapping_failure`, `cfg.mapping`. |
| Fixtures | `tests/fixtures/generic-webhook/` | Contract examples with `.source` files; Azure/Atlas await C5 (see `tests/fixtures/README.md`). |

## Acceptance scenarios (spec §22)

| Scenario | Test |
|---|---|
| Duplicate webhook delivery | `ProcessingScenarios.Scenario_DuplicateWebhookDelivery_*` — 1 applied_event, 1 delivery_duplicate (count 2 for three deliveries), occurrence count 1, no version bump. |
| Two workers process one condition | `Scenario_TwoWorkersProcessOneCondition_*` — 8 concurrent openings ⇒ one episode, count 8, all events applied. |
| Recovery arrives before opening | `Scenario_RecoveryArrivesBeforeOpening_*` — marker written, delayed opening recorded `late`, no episode, a later firing opens normally. |
| Old recovery during a new episode | `Scenario_OldRecoveryDuringNewEpisode_*` — episode B stays firing/new, event recorded `late_event` on B, A untouched. |
| Historical replay executes (no-transition part) | `Scenario_HistoricalReplay_*` — zero transitions, zero new episodes, zero timers, events tagged `replayed`. |
| Manual close while source is firing (processing half) | `Scenario_ManualCloseWhileSourceIsFiring_*` — closed episode keeps condition `firing` + `manual_close`/`human`; the next firing opens a linked new episode. |

Unit coverage: reference Azure CAS and Atlas mappings from 07 §2–3 normalise end to end (incl. JSONPath filter, headers, enrich tables); DSL validation errors carry paths; fingerprint property tests (FsCheck): dictionary order independence and any changed component changes the hash; canonical JSON; version comparison; ordering with skew; episode state machine.

## Decisions made

- **Duplicates are not stored as `normalised_event` rows** — the raw bytes are retained and the ledger + counter record the retransmission; this keeps the hot table proportional to effective signals.
- **Unknown `event_type` is a mapping failure**, not a silent default: the example in 07 §1 shows `default: unknown` on the lookup, but applying an unknown type would have to guess between opening and closing. Conservative per AGENTS rule 11.
- **Producer `acknowledged` events are recorded on the timeline only** (spec §15.3: distinguish condition changes from user responses); handling state is never changed by a source.
- **A `resolved` with no open episode** writes the closed-instance marker and, when a closed episode exists for the identity, a `late_event` on it for visibility; no episode is opened or reopened.
- **A firing that is older than the previous episode's latest signal is late**; otherwise it opens a new episode linked via `previous_episode_id`, including after a manual close.
- **Historical replay of an already-applied key** adds one `replayed` timeline entry to the episode it belongs to; a replayed never-seen key is written to the ledger as `replayed` so a later live delivery is a duplicate rather than a transition.
- **Timers and notifications are not scheduled yet**: the transition hook is a no-op until routing (M3) and lifecycle policies (M5) exist; `owning_team_id` defaults to the integration owner.
- **Argon2/NFC need ICU**: `InvariantGlobalization` is off so `string.Normalize` performs NFC (the runtime images ship libicu).
- **Azure `azure_subscription` transform** accepts any `/subscriptions/{id}` segment, not only GUIDs, so provisional samples work.
