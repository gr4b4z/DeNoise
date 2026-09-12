# Milestone 10 — Policies UI, config-as-code, audit screen

Status: **backend done**; UI half in progress (08 §3.6 policies screens, config export/import page, audit screen).

## What exists

| Area | Where | Notes |
|---|---|---|
| Policy API (since M3/M5) | `ConfigEndpoints` `/api/v1/policies/{kind}` | List active versions (with YAML), versions of one policy, create a version (validated for its kind, inactive until activated), activate, impact preview (lifecycle: recomputed deadlines; routing: episodes whose team would change), rollback (= activate an earlier version, ADR-6). Now covered over HTTP by `Milestone10/ConfigAndAuditApiTests`. |
| Activation fix | `PolicyService.ActivateAsync`, `IUnitOfWork.InTransactionAsync` | Rolling back to a version that had been active before failed with a unique-index violation: `policy_one_active_version` is a **partial** unique index and EF does not order updates by a filtered index, so the re-activated row could be written before the deactivated one. Activation now flushes the deactivation first, inside one transaction (`EfUnitOfWork.InTransactionAsync`, nested calls join the outer transaction). |
| Config export | `GET /api/v1/config/export` (`episode.read`, `application/yaml`) | One YAML bundle: `alerthub_config: 1`, `policies: { routing: [...], escalation: [...], lifecycle: [...], grouping: [...] }`, each entry `{ id, name, version, document }` with the **active** version's document (06 §8: DB is truth, YAML in, YAML out). Deterministic order (name, id) so exports diff cleanly in git. |
| Config import | `POST /api/v1/config/import` `{ yaml, dryRun }` (`policy.manage`, CSRF) | Same shape back in. Per entry the document is compared canonically (sorted keys) with the active version: `unchanged`, `new_version` (with a top-level field diff `+ key` / `- key` / `~ key`) or `create`. `dryRun: true` (the default) writes nothing; `dryRun: false` creates **and activates** the differing versions through the same `PolicyService` path as the UI (validation, audit, activation hooks). Import never deletes and never touches policies the bundle does not mention. Routing and grouping default to their well-known ids; escalation and lifecycle entries need an `id` so re-imports address the same policy. Document-level problems (unknown kind, not a list, missing `id`, validation failures) come back as `errors[]` with paths; the response is `200` with the structured diff either way, the HTTP status is not the verdict. |
| Audit log | `GET /api/v1/audit` (`audit.read`) → `PagedResponse<AuditEntryDto>` | Newest first, keyset paging on `(at, id)` (`cursor`), `limit` ≤ 500. Filters: `target` (target id), `targetType`, `actor` (id or display name), `action` (exact or dotted prefix: `policy` matches `policy.lifecycle.activate`), `from` (inclusive), `to` (exclusive). `before`/`after` are returned as JSON. Uses the existing `audit_target_idx` / `audit_actor_idx` / `audit_at_idx`. |
| Contracts | `Contracts.cs` | `ConfigImportRequest`, `ConfigImportEntryDto`, `ConfigImportResponse` (`hasChanges`), `AuditEntryDto`; OpenAPI snapshot and generated client regenerated. |

## Acceptance (09 M10 exit criteria)

| Criterion | Test |
|---|---|
| Policy version, activate, impact, rollback over the API; invalid documents 400; operators 403 | `ConfigAndAuditApiTests.Policy_versions_activate_impact_and_rollback_over_http` |
| Export → import of the same bundle is a no-op; an edited document dry-runs as `new_version` with the field diff and writes nothing; applying creates and activates v2; the next export carries the change; malformed bundles report errors; export readable by operators, import not | `Export_import_round_trip_is_unchanged_and_edits_create_new_active_versions` |
| Audit lists newest first, filters by target / type / actor / action prefix / time, pages without gaps or duplicates, viewers can read | `Audit_log_lists_newest_first_with_filters_and_keyset_paging` |

## Decisions made

- **Import activates.** A bundle describes the desired active configuration; leaving the imported versions inactive would make "apply" a two-step manual job and defeat config-as-code. The dry run is the review step.
- **Import is additive.** Removing a policy from the bundle does not deactivate it; deletion of configuration stays an explicit UI/API action with its own audit entry.
- **Top-level diff only in the API.** The review table needs to say *what* changed; the line-by-line view is the editor's diff against the stored YAML.
- **One bundle version field** (`alerthub_config: 1`) so a future shape change can be recognised instead of misread.
