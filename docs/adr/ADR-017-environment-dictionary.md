# ADR-17: Environment classes are a server-side dictionary

**Status:** Accepted. Implements the "or another configured value" of spec §9 and §15.5, which until now had nothing to configure.

## Context

`environment` is a canonical field derived by each integration's mapping (spec §15.5 rule 1). It drives the default queue view,
routing, lifecycle, grouping and suppression predicates, and per-environment reporting. Before this decision:

- the backend accepted any string, both from mappings and in the queue filter;
- the frontend hard-coded four chips (`production`, `staging`, `development`, `unknown`), so a class such as `uat`, or the `ci`
  and `ephemeral` values the pipeline reference mappings (M16) already produce, had no chip;
- a typo in a mapping (`prodcution`) was saved without warning. The episode then dropped out of the default
  `production + unknown` view, which exists to catch precisely that kind of unclassified alert.

Options considered:

1. **Derive the list from data** (`SELECT DISTINCT environment`). Gives a typo its own chip and makes it look intentional,
   hides a class with no open episodes, and leaves policy authors with nothing to validate against.
2. **Keep a frontend constant and extend it on demand.** Every new class becomes a frontend release, and the backend still
   cannot tell a class from a typo.
3. **A controlled dictionary owned by the Hub**, validated where values are authored and exposed to the UI.

## Decision

Option 3:

- `cfg.environment (key, label, sort_order, is_built_in, version, …)`. A key is a lower-case identifier
  (`^[a-z][a-z0-9_-]{0,31}$`, enforced by a check constraint), so the generic mapping's `transform: [lower, trim]` lands on it.
  The migration seeds `production`, `staging`, `development`, `ci`, `ephemeral` and the built-in `unknown`.
- An entry names an environment **class**, never one product's instance of it. The product is the integration's
  `access_scope` (spec §19.2); the instance is the integration plus the `scope` identity component (spec §10.2).
- `GET /api/v1/environments` for every signed-in user. `POST`, and `PUT`/`DELETE` with `If-Match`, need `policy.manage`,
  because environment classes are inputs to policy. Every change is audited (`environment.create|update|delete`).
- Keys are immutable. Renaming means adding the new class, re-pointing the mappings, and deleting the old class.
- `unknown` is built in and cannot be deleted. A class still produced by an active mapping cannot be deleted either.
- **Validation where values are authored.** Saving or activating a mapping version, and previewing a draft, reject any
  environment the mapping can produce statically (constants, `lookup.map` / `regex_map` targets, defaults, lookup tables an
  `enrich` step writes into `environment`) that is not in the dictionary. Errors carry the editor path. Values copied from the
  payload (`path`) cannot be checked at save time.
- **Ingest never refuses** (spec §15.5): a value outside the dictionary is stored as received. The queue's `unknown` filter
  also matches such values, so they show in the default view. Filtering on the raw value through the URL still works.
- The default view stays `production + unknown`, as spec §15.5 rule 4 fixes it. It is not a per-entry setting.
  `inDefaultView` on the DTO reports it so the UI does not need its own copy.

## Consequences

- The queue builds its chips from the dictionary. A value that a URL or saved filter carries but the dictionary lacks is still
  shown, marked as not configured.
- Adding `uat` is a configuration change on the `/environments` screen or through the API, not a release.
- Routing, lifecycle, grouping and suppression predicates that compare `environment` are **not** validated against the
  dictionary yet. A policy that names a missing class matches nothing, as before. Extending policy validation is a follow-up.
- A deleted class leaves episodes that carry its key. From then on they count as unclassified and move into the default view.
  This is the conservative outcome rule 11 of AGENTS.md asks for.
- The seed is frozen in the migration. `EnvironmentClasses.Seeded` mirrors it, and a unit test holds every reference
  mapping to it, so a new reference mapping that invents a class fails CI instead of failing integration creation.
