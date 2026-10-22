# GitHub as an alert source (milestone 16)

GitHub webhooks post workflow and deployment results to DeNoise; a failed run opens an episode, the next successful run
of the same workflow resolves it. Ownership, routing, grouping, flapping and notifications are the existing machinery.
Design and decisions: `docs/milestones/M16-pipeline-sources.md`; authentication: `docs/adr/ADR-016-signature-as-credential.md`.

## 1. Create the integration

Wizard → type **GitHub**. Enter a **webhook secret** (generate a long random one). GitHub cannot send an
`Authorization` header, so the secret is the credential: DeNoise stores it encrypted, requires
`X-Hub-Signature-256` on every delivery and verifies the HMAC-SHA256 of the raw body (ADR-16). The bearer token shown
once at creation still works for `curl` tests.

Do **not** switch the signature to optional for a GitHub integration: without a bearer, an integration whose
signature is optional refuses every GitHub delivery with 401 (the signature-only path exists only for `required`).

One integration per GitHub **organisation** (or per repository for a pilot) is the recommended cut.

## 2. Add the webhook

Repository or organisation → Settings → Webhooks → *Add webhook*:

- Payload URL: the integration's ingest URL
- Content type: `application/json` (the signature covers the raw JSON body)
- Secret: the one entered in the wizard
- Events: *Let me select individual events*

| Event | Mapping | When to use |
|---|---|---|
| Workflow runs (`workflow_run`) | `github-workflow-run` | default: one condition per workflow (and per branch for builds) |
| Workflow jobs (`workflow_job`) | `github-workflow-job` | only for a workflow whose **jobs are the environments**; then exclude that workflow's `workflow_run` with an `applies_when` on the integration's mapping, so one run never becomes two episodes |
| Deployment statuses (`deployment_status`) | `github-deployment-status` | teams that use GitHub Environments: one condition per repository and environment |
| Deployments (`deployment`) | `github-deployment` | optional; recorded without an episode |

GitHub sends `requested` and `in_progress` deliveries for `workflow_run` too; they are informational and open nothing.
The `ping` GitHub sends on creation is recorded the same way. Any other event (`push`, `check_run`, …) has no mapping
and lands in the integration's **Failures** tab with the event name.

## 3. What the reference mappings do

| | build | deploy (GitOps, `helm upgrade --atomic`) | promote |
|---|---|---|---|
| recognised by the workflow name | `*-ci`, `ci-*`, `build-*`, anything unrecognised | `deploy-*`, `*-cd`, `*-cd-<env>`, a **job** named after the environment, or a GitHub Environment (`deployment_status`) | `promote-*`, `*-promotion` |
| `environment` | `ci` | class of the target: `production`, `staging`, `development`, `ephemeral` (`pr-*`), else `unknown` | same, from the name |
| identity | repository, `ci`, workflow, `build-failed`, **branch** | repository, environment, workflow or GitHub Environment, `deploy-failed` — no branch | repository, environment, workflow, `promotion-failed` |
| severity | `main`/`master`/`release/*`/`hotfix/*` → `high`; `pull_request` runs → `informational`; other branches → `low` | `production` → `high`, `staging` → `medium`, `development`/`ephemeral` → `low`, `unknown` → `medium` | as deploy |

The naming convention is an **assumption** until the real names arrive; it lives in the `labels.kind` and
`environment` lookups of each mapping and is edited per integration in the mapping editor.

Ordering uses `workflow_run.updated_at` / `workflow_job.completed_at`; `source_version` is deliberately unset
(`run_attempt` restarts per run). Idempotency uses `X-GitHub-Delivery`, so *Redeliver* in GitHub's webhook log is a
duplicate and does nothing.

## 4. Seeing CI failures

Builds carry `environment: ci`, so the default queue view (`production + unknown`) does not show them. Open the queue
with `environment=ci` and save it as a filter. Deployments carry their target environment.

## 5. Noise controls that apply

- **Retries are occurrences**: *Re-run failed jobs* on a red run increments `occurrence_count`; the first success
  resolves.
- **Flapping** (M13) and **grouping** (M15, `key: [resource_id, labels.run], min_members: 2`) as for Azure DevOps.
- `cancelled`, `skipped`, `neutral`, `stale` and `action_required` conclusions are recorded without an episode.
- The explicit-recovery backstop closes an episode after 60 minutes of silence (spec §12.2); lengthen
  `auto_resolve.after_silence` on the integration's lifecycle policy for workflows that are not re-run quickly.

## 6. Fixtures

`tests/fixtures/github/PROVISIONAL-*.json` follow the shapes documented at
<https://docs.github.com/en/webhooks/webhook-events-and-payloads> and are replaced by redacted captures from the
webhook delivery log as soon as the first repository is connected (AGENTS.md §5.9).
