# Milestone 16 — CI/CD pipeline sources: Azure DevOps and GitHub

Status: **Phase A implemented** (PR #3); Phase B not needed so far; Phase C deferred (decision 5). Prerequisite: M8 (integrations, reference mappings, HMAC verification), M13 (flapping builds are one episode), M14 (a failing deployment can be the parent of the alerts it causes).

## Problem

Pipeline and deployment failures are operational conditions like any other alert: they need an owner, an acknowledgement
deadline, deduplication across retries, automatic resolution when the next run succeeds, and a place in the same queue the
team already watches. Today they live in Azure DevOps and GitHub notifications only, so a failing release on `main` is
noticed by whoever happens to read the mail.

DeNoise already ingests any authenticated JSON webhook through a declarative mapping (07 §1); Azure DevOps Service Hooks and
GitHub webhooks are JSON webhooks. **This milestone adds two integration types with reference mappings, not a new
pipeline.** Everything after the mapping — identity, episodes, routing, lifecycle, grouping, suppression, outbox — is reused
unchanged, which is the whole point of treating them as "another alert source".

## Scope boundary (decided up front)

In:

- Integration types `azure_devops` and `github` with seeded reference mappings, wizard entries and documentation of the
  webhook set-up on the producer side.
- Azure DevOps events: `build.complete`, `ms.vss-pipelines.run-state-changed-event`, `ms.vss-pipelines.stage-state-changed-event`
  (YAML pipelines), `ms.vss-release.deployment-completed-event` (classic releases).
- GitHub events: `workflow_run` (`action = completed`), `workflow_job` (`action = completed`), `deployment`,
  `deployment_status`.
- Webhook authentication that these producers can actually perform (see *Security*).
- Classification build vs deployment, branch tiering and environment class resolution **expressed in the mapping and the
  policies**, not in code.

Out (spec §5.2 and AGENTS.md §5.10):

- Pull request and code review events, fetching logs, any call to the Azure DevOps or GitHub API in the ingestion path,
  storing build logs. The episode records the fact and a link (`source_url`), never the log.
- A "tenant" concept. One integration per Azure DevOps organisation + project and per GitHub organisation (or repository),
  separated by `access_scope`, exactly as §15.5 separates environments. Spec §5.2 excludes multi-tenant operation.
- A separate "light tier" that is not persisted. AGENTS.md §5.3 forbids acknowledging an event that is not committed, and
  the raw-event partition is the audit trail. Noise is controlled by severity, informational type, routing and grouping
  (below), never by dropping events before commit.
- New `event_type` values. `pipeline.failed`, `deployment.succeeded` and friends are **rule identities**, not event types;
  the fixed vocabulary of 04 §2.1 (`firing`, `update`, `resolved`, `cancelled`, `informational`, `heartbeat`) already
  expresses every pipeline transition (table below).
- Publishing to a downstream queue. Consumers (the AI DevOps platform included) are destinations of the outbox (§16.4)
  subscribing to the event types they want; the notification model carries `labels` and `dimensions`.
- Environment lifecycle automation (post-deployment warm-up suppression, environment teardown closing episodes). Those are
  new behaviours the specification does not describe; they are listed under *Phase C* with the amendment they need and
  wait for owner sign-off (AGENTS.md §5.1, §5.11). Nothing in Phases A–B depends on them.

## How pipeline events map onto the existing model

| Producer event | Condition | `event_type` | `rule_id` | identity | severity |
|---|---|---|---|---|---|
| ADO `build.complete`, GitHub `workflow_run` | result `failed` / `partiallySucceeded` / `failure` / `timed_out` | `firing` | `pipeline-failed` | environment, pipeline id, rule, dims `{branch}` | by branch class (table below) |
| same | `succeeded` / `success` | `resolved` | `pipeline-failed` | same as the failure, so the open episode resolves (04 §2.1 row 3, evidence `source`) | — |
| same | `canceled` / `cancelled` / `skipped` | `cancelled` | `pipeline-failed` | same | — |
| ADO `stage-state-changed` (result failed), GitHub `workflow_job` (conclusion failure) | a stage/job failed | `firing` | `pipeline-failed` | as above plus dims `{branch, stage}` — one episode per failing stage; the run-level event groups them (M15 key `[resource_id, run]`) | by branch class |
| ADO `release.deployment-completed` (status failed), GitHub `deployment_status` (`failure`, `error`) | deployment failed | `firing` | `deployment-failed` | **environment**, resource = repo/release definition, rule, no branch | by environment class (`production` → `high`, `staging` → `medium`, else `low`) |
| same, status succeeded | deployment recovered | `resolved` | `deployment-failed` | same | — |
| GitHub `deployment`, `deployment_status` (`pending`, `in_progress`, `queued`), ADO `run-state-changed` (`inProgress`) | a fact, not a condition | `informational` | `deployment-started` / `run-started` | environment, resource, rule, dims `{run}` | `informational` |

Rules this table encodes:

- **Success resolves failure** only if both carry the same identity, so the error signature is **not** an identity component
  (the prompt's "fingerprint minus error_signature" is exactly this). The first failing step or task name goes into `summary`
  and `labels.error_signature`; stripping GUIDs and durations from it is cosmetic and done with `lookup.regex_map` /
  `truncate`, never with a hash.
- **Environment is part of the identity for deployments**: the same failure in `pr-123` and `pr-124` is two episodes. For
  builds the environment is the constant `ci` (decision below) and the branch is a dimension, so `main` and `feature/x`
  never share an episode.
- **Retries are occurrences.** A re-run of the same failing run on the same branch is the same identity → `occurrence_count`
  increments, nobody is notified twice; a flapping pipeline (fail, pass, fail) is one episode with a reopen count under M13
  once the owner opens a reopen window for the integration.
- **Ordering** uses `source_version` = GitHub `run_attempt` / ADO build `id` + `lastChangedDate` so a late `completed` for
  attempt 1 never reopens an episode attempt 2 resolved (04 §5).
- **Idempotency** uses the producer's delivery id: GitHub header `X-GitHub-Delivery` (`{ header: }` rule), Azure DevOps
  `$.id` + `$.subscriptionId` (`template`). The `applied_event` ledger already turns redeliveries into duplicates.

### Branch tiering and environment classes, in the mapping

The prompt's "tiers" are severity decisions and belong in the mapping's `lookup.regex_map`, which operators edit per
integration without a release:

```yaml
severity:
  lookup:
    from: "$.resource.sourceBranch"            # GitHub: $.workflow_run.head_branch
    regex_map:
      "^refs/heads/(main|master)$": high
      "^refs/heads/(release|hotfix)/.*$": high
      "^refs/pull/.*$": informational           # PR validation: queue only, informational expiry (spec §12.5)
      ".*": low                                 # feature branches: queue only (spec §9 severity table)
    default: low
environment:
  lookup:
    from: "$.resource.environment.name"        # deployments; builds use { const: ci }
    regex_map:
      "^(prod|production)(-.*)?$": production
      "^(stg|staging|uat)(-.*)?$": staging
      "^(pr|eph)-.*$": ephemeral
      "^dev(-.*)?$": development
    default: unknown
```

`low` and `informational` are *queue only* by the spec's severity table, so feature-branch failures are visible, searchable
and counted, but never notify or escalate — the same outcome the prompt wanted from a "light tier", without an unaudited
side table. Routing policies decide the team per integration or per `labels.pipeline`; grouping policies (M15) collapse a
storm of failing jobs of one run into one notification (`key: [resource_id, dimensions.run]`, `min_members: 2`).

## Pipeline kinds — the three shapes that exist here

The owner's estate has three kinds of pipeline; they need two identity patterns, not three mechanisms.

| Kind | What a run means | Identity | Severity | Notes |
|---|---|---|---|---|
| **build** (artifact pipelines; from feature *and* protected branches; some also push to `dev`) | the artifact built or did not | `environment: ci`, pipeline, rule `build-failed`, dims `{branch, stage}` | by branch class: protected → `high`, PR validation → `informational`, other → `low` | a build that also deploys to `dev` stays a build; a chain of red runs on one branch is one episode (occurrences), red-green-red is flapping (M13) |
| **deploy** (GitOps, `helm upgrade --atomic`) | **the deployment itself**: success = chart installed and pods Ready; failure = rolled back to the previous release | pipeline, **target environment**, rule `deploy-failed`, dims `{chart}` when a stage deploys several charts | by environment class: `production` → `high`, `staging` → `medium`, `development` / `ephemeral` → `low` | `--atomic` waits for readiness, so the pipeline result *is* the deployment result: no controller signal, no "deployment started" events, no warm-up needed. The `summary` says *rolled back to the previous release*; a failure before the helm step (lint, tests, credentials) is the same identity with another `labels.failed_step`. `occurred_at` is the run's end, so a long rollout orders correctly (04 §5) |
| **promote** (copy an artifact from one place to another) | the artifact is or is not in the target | pipeline, **target environment/stage**, rule `promotion-failed`; the artifact version is a label, never identity | `production` → `high`, else `medium` | one episode per target until a promotion succeeds, whatever the versions tried in between |

Across all three: **only a failure opens an episode, a success only resolves one, a start opens nothing.** Starts,
cancellations and skips that do arrive (GitHub sends `requested` / `in_progress` for every `workflow_run`) are mapped to
`informational` *without* a `rule_id`, so the identity cannot resolve and the event is recorded without an episode
(04 §2.1) — nothing new in the vocabulary, nothing in the quarantine. A green day produces resolved events that find no
open episode and are recorded as such.

Kind and target environment come from the pipeline *name* (payloads carry no tags and the ingestion path makes no API calls),
through one `lookup.regex_map` in the reference mapping that operators adjust per integration. **Assumed naming convention
until the real names arrive** (owner input, see below):

| Pattern (pipeline / workflow name) | Kind | Environment |
|---|---|---|
| `*-ci`, `ci-*`, `build-*` | build | `ci` |
| `*-cd-<env>`, `deploy-<env>-*`, or a stage named after the environment | deploy | `<env>` through the environment class lookup |
| `promote-*`, `*-promotion` | promote | from the stage / target parameter, else `unknown` |
| anything else | build | `ci` |

Unknown names therefore fall back to the least noisy reading (a build on its branch class), never to a production deployment.

**Which webhooks to subscribe, so one run never becomes two episodes:** Azure DevOps `build.complete` for single-stage
builds and promotions, `stage-state-changed` for multi-stage deploy pipelines where stage = environment (then *not*
`run-state-changed` for the same pipeline); GitHub `workflow_run` (completed) by default, `workflow_job` only for a
workflow whose jobs are the environments (then `applies_when` excludes that workflow's `workflow_run`); `deployment` /
`deployment_status` only for teams that use GitHub Environments. Service Hook status filters: Failed, PartiallySucceeded,
Succeeded.

## Security — what the producers can actually do

| Producer | Can send | DeNoise today | Change |
|---|---|---|---|
| Azure DevOps Service Hook (Web Hook) | custom HTTP headers, Basic auth | bearer token in `Authorization` per integration | none: the subscription sets `Authorization: Bearer <token>`; documented in the set-up guide |
| GitHub webhook | **only** `X-Hub-Signature-256: sha256=<hex>` over the raw body, no custom headers | bearer token **required** before the signature is checked (`IngestEndpoints.HandleAsync`); `IngestSignatureVerifier` compares the raw header value, so the `sha256=` prefix fails the decode | **ADR-16 "Signature as credential"**: when an integration is configured `hmac.required = true`, a request without a bearer token is authenticated by the signature alone (the key id in the URL selects the integration, the HMAC proves the sender). The token stays mandatory for every other integration. The verifier learns to strip an `<algorithm>=` prefix. |

Both remain signed by the per-integration secret stored encrypted (AGENTS.md §5.6); nothing else about ingestion changes.
Replay of a captured GitHub delivery is a duplicate by delivery id and does nothing, as today.

## Phases

### Phase A — the two sources (one PR per type, generic infrastructure first)

1. **`IntegrationTypes.AzureDevops`, `IntegrationTypes.Github`** in Domain; `LifecycleProfiles.DefaultFor` → `explicit_recovery`
   for both (every run reports its own end; a lost `completed` webhook is backstopped by the 60-minute inactivity timer, as
   for Azure Monitor). Wizard entries in `web/src/integrations/queries.ts` with the profile; no new wizard steps.
2. **ADR-16 + ingest change**: signature-as-credential for `hmac.required` integrations; `sha256=` prefix tolerance; the
   wizard's HMAC step pre-fills `header: X-Hub-Signature-256, algorithm: sha256, encoding: hex, required: true` for GitHub.
   Unit tests on the verifier (prefix, wrong secret, missing header with and without a bearer).
3. **Reference mappings** in `ReferenceMappings` (07 §2 pattern, ordered `applies_when` list per type):
   - `azure-devops-build` (`eventType == build.complete`), `azure-devops-yaml-run`, `azure-devops-yaml-stage`,
     `azure-devops-release-deployment`;
   - `github-workflow-run` (`$.workflow_run` present, `action == completed`), `github-workflow-job`,
     `github-deployment-status`, `github-deployment`.
   Each with `source_alert_id`, `source_version`, `occurred_at`, the identity block, `labels` (`pipeline`, `stage`, `job`,
   `actor`, `error_signature`, `run_url`, `commit`), `dimensions` (`branch`, `run`), `source_url` and the severity /
   environment lookups above. Unknown event types fall through to no mapping → quarantined in the failure queue with the
   event name, visible in the integration's Failures tab (M8), never silently dropped.
4. **Fixtures** under `tests/fixtures/azure-devops/` and `tests/fixtures/github/`: one `.json` + `.source` per event in the
   table, `PROVISIONAL-` from vendor documentation until the first captures replace them (AGENTS.md §5.9). Mapping samples in
   the reference mappings point at them so `pnpm`-free preview and the mapping unit tests share the same bytes.
5. **Scenarios** (`tests/DeNoise.Integration.Tests/Milestone16/PipelineScenarios.cs`), one test per row:
   - *Pipeline fails on main, then succeeds* — one episode, `high`, owner routed, resolved with evidence `source`, closed
     notification to prior recipients.
   - *Pipeline fails on a feature branch* — `low`, visible in the queue, no outbox row.
   - *Three jobs of one run fail* — three episodes, one group, one notification (M15 `min_members: 2`).
   - *Re-run of a failing build* — same identity, `occurrence_count` 2, no second notification.
   - *Deployment fails in production, same release fails in staging* — two episodes by environment, `high` and `medium`.
   - *Late completion of attempt 1 after attempt 2 resolved* — recorded as late, episode stays closed (04 §5).
   - *GitHub delivery without a bearer but with a valid signature is accepted; with a wrong signature 401; redelivery
     (same `X-GitHub-Delivery`) is a duplicate.*
   - *Azure DevOps subscription authenticates with the bearer header; a payload with an unknown `eventType` is quarantined.*
6. **Docs**: `docs/integrations/azure-devops.md` and `docs/integrations/github.md` (which events to subscribe, URL, header or
   secret, how `sourceBranch` and `environment` become severity and environment, how to override the lookups per
   integration), the 06 contract note for the new types, CHANGELOG, and the fixtures README rows.

### Phase B — DSL gaps found while writing the mappings (only if needed)

Expected candidates, each a small, tested addition to `MappingEngine` with its 07 §1 row:

- transform `regex_replace:<pattern>=<replacement>` for cosmetic error-signature cleaning (GUIDs, durations) if
  `lookup.regex_map` proves too coarse for `summary`;
- `header:` rule already exists; a `headers_all` is **not** needed;
- `first_nonempty` across `$.resource.stage.name` / `$.resource.job.name` already covers the stage/job fallbacks.

If none is needed, Phase B is empty. No new predicate operators are expected.

### Phase C — environment lifecycle (gated on a spec amendment; separate decision)

Two of the prompt's rules are new behaviour, not mapping:

1. **Warm-up after a deployment**: alerts for environment *X* muted for *n* minutes after `deployment-started` /
   `deployment-succeeded`. Mechanism already exists — a maintenance window with scope `environment == X` (M9) — but
   creating it from an ingested event is an **ingest-driven suppression**, which §16.3 does not describe (windows need an
   actor and a reason). Proposal for the amendment: a *deployment hook* policy kind (`on: deployment-started`,
   `environment`, `duration ≤ 30 m`, reason template) whose windows carry actor `integration:<id>` and are listed, audited
   and cancellable like any other; the summary at the end names the deployment. Default: no hook configured.
2. **Environment teardown** (`environment.destroyed`): closing every open episode of an ephemeral environment. Closure
   reason would have to be new (`environment_removed`) because `source_cancelled` is per identity and `manual_close` needs a
   person; spec §11.2 would gain the reason and §12.5 the rule that it is never displayed as recovery. Until then an
   ephemeral environment's episodes expire administratively under the lifecycle policy of the integration (short
   `unverified_expire_after` for `environment_class = ephemeral`, set per routing rule), which is already configurable.

Both wait for the owner's sign-off on the amendment text before any code (AGENTS.md §5.1); they are not required for the
Phase A acceptance scenarios.

## Decisions made (owner, 2026-10-21)

1. **A failed production deployment with an atomic rollback is `high`** (`staging` → `medium`, lower classes → `low`). The
   environment keeps running the previous release, but a failed production deployment is work for a person now.
2. **Builds carry `environment: ci`.** It keeps the identity honest and lets operators route CI noise separately. Consequence:
   the default queue view (`production + unknown`, §15.5) does not show build failures; the integration docs recommend a
   saved filter or an entry-point link including `environment=ci`. Alternative rejected: setting `production` on `main`
   builds would be a lie that pollutes the production view and the G1 numbers.
3. **ADR-16 signature-as-credential** for `hmac.required` integrations is the GitHub authentication path; never a token in
   the URL query, no proxy in front of DeNoise.
4. **PR validation runs are `informational`, feature-branch builds `low`**, persisted and queue-only: they expire or sit in
   the queue, searchable, which is what the "flaky counter" was for. Nothing is dropped before commit.
5. **Phase C is deferred.** `--atomic` already waits for readiness, so no warm-up; ephemeral environments expire
   administratively through a short `unverified_expire_after` on the routing rule for `environment_class = ephemeral`.
6. **No new event types, no "pipeline" severity scale.** The spec's severity table applies as is.
7. **Pipeline names follow the assumed convention above until the real names arrive**; the lookups are per integration and
   change without a release.

### Decisions made while implementing Phase A (listed per AGENTS.md §5.11)

8. **A cancelled or stopped run is recorded without an episode**, not as a `cancelled` event: it proves nothing about the
   build and must not close an open failure. Same for GitHub `skipped`, `neutral`, `stale`, `action_required` and the
   transient `deployment_status` states.
9. **A deploy or promote pipeline whose target cannot be classified gets `environment: unknown` and `severity: medium`**:
   unknown stays visible in the default view (§15.5) and a person decides; it is never read as production.
10. **`source_version` stays unset** for both sources: run ids repeat across re-runs of one run (ADO retry, GitHub
    `run_attempt`) and attempts restart per run, so the run's finish time orders instead (04 §5 fallback).
11. **Spec / implementation gap found and closed (owner: "implement everything"):** the spec's severity table makes
    `low` *queue only*, but the notification hook gated only `informational`. `Episode.IsQueueOnly`
    (`!IsActionable || severity == low`) now gates the opening notification, the acknowledgement deadline and the
    reopen notification; a `low` episode that later rises out of `low` is announced as a new actionable episode
    (`episode.opened` with `queueOnlyBefore`, deadline scheduled then). Routing, ownership, grouping, suppression and
    topology attribution are unchanged for `low`, so a low dependent still hangs under its parent — it just has no
    rows to mute (M14 scenario adjusted). Feature-branch failures therefore really are queue only.
12. **The 60-minute explicit-recovery backstop stays** (spec §12.2). The integration guides tell operators to lengthen
    `auto_resolve.after_silence` for pipelines that are not re-run quickly; high episodes get the stale-state escalation
    before closure either way.

## Open owner inputs

- The real pipeline / workflow naming convention (or three or four real names per kind) — replaces the assumed table.
- Which Azure DevOps organisations/projects and GitHub organisations go first (one integration each; names decide
  `access_scope` and the owning team's routing rule).
- Real captures for the events to replace the `PROVISIONAL-` fixtures (C5 pattern): at least `build.complete` failed,
  `stage-state-changed` failed and `workflow_run` completed/failure.

## Effort

Phase A: backend ~3 days (types, ADR-16 ingest change, eight mappings, scenarios), docs ~1 day, UI ~0.5 day (wizard rows,
HMAC pre-fill). Phase B: 0–1 day. Phase C: ~3 days after the amendment is merged.
