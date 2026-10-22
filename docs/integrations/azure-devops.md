# Azure DevOps as an alert source (milestone 16)

Azure DevOps **Service Hooks** post pipeline results to DeNoise; a failed run opens an episode, the next successful run of
the same pipeline resolves it. Nothing else changes: ownership, routing, grouping, flapping and notifications are the
existing machinery. Design and decisions: `docs/milestones/M16-pipeline-sources.md`.

## 1. Create the integration

Wizard → type **Azure DevOps**. Access scope and owning team as for any source; the lifecycle preset is
`explicit_recovery` (every run reports its own end; a lost delivery is backstopped by the inactivity timer, spec §12.2).
The ingest URL and bearer token are shown once.

One integration per Azure DevOps **project** is the recommended cut (the project is the first identity component and the
natural `access_scope`).

## 2. Subscribe the Service Hooks

Project settings → Service hooks → **Web Hooks**, one subscription per event:

| Event | Mapping | Filters to set | When to use |
|---|---|---|---|
| Build completed (`build.complete`) | `azure-devops-build` | Build status: *Failed*, *Partially succeeded*, *Succeeded* | single-stage builds and promotions; classic and YAML |
| Run state changed (`ms.vss-pipelines.run-state-changed-event`) | `azure-devops-yaml-run` | Run state: *Completed* | whole-run result of a YAML deploy pipeline whose name carries the target |
| Run stage state changed (`ms.vss-pipelines.stage-state-changed-event`) | `azure-devops-yaml-stage` | Stage state: *Completed* | multi-stage pipelines whose **stages are the environments** (then do **not** also subscribe the run event for that pipeline) |
| Release deployment completed (`ms.vss-release.deployment-completed-event`) | `azure-devops-release-deployment` | Status: *Failed*, *Partially succeeded*, *Succeeded* | classic releases |

Action settings: URL = the integration's ingest URL; **HTTP headers** = `Authorization: Bearer <token>`;
resource details *All*, messages *None* is enough. Leave *Send as* at the default (JSON).

Subscribe success events too: a success is what resolves the open episode. Starts (`inProgress`) are not needed; if
they arrive anyway they are recorded as informational and open nothing. Any other event type (`git.push`,
`workitem.*`, approvals) has no mapping and lands in the integration's **Failures** tab with the event name, so a
mis-subscribed hook is visible, never silent.

## 3. What the reference mappings do

| | build | deploy (GitOps, `helm upgrade --atomic`) | promote |
|---|---|---|---|
| recognised by the pipeline name | `*-ci`, `ci-*`, `build-*`, anything unrecognised | `deploy-*`, `*-cd`, `*-cd-<env>` or a **stage** named after the environment | `promote-*`, `*-promotion` |
| `environment` | `ci` | class of the target: `production`, `staging`, `development`, `ephemeral` (`pr-*`), else `unknown` | same, from the name |
| identity | project, `ci`, pipeline, `build-failed`, **branch** | project, environment, pipeline (and stage), `deploy-failed` — no branch | project, environment, pipeline, `promotion-failed` |
| severity | protected branch (`main`, `master`, `release/*`, `hotfix/*`) → `high`; PR validation → `informational`; other branches → `low` | `production` → `high`, `staging` → `medium`, `development`/`ephemeral` → `low`, `unknown` → `medium` | as deploy |
| `rule_name` | Build failed | Deployment failed | Promotion failed |

The naming convention is an **assumption** until the real names arrive; it lives in two `lookup.regex_map` blocks
(`labels.kind` and `environment`) of each mapping and is edited per integration in the mapping editor — a new mapping
version, verified against the stored samples, no release. Everything the queue shows is text: pipeline names, branch
names and messages are never rendered as markup.

Ordering uses the run's finish time (`occurred_at`); `source_version` is deliberately unset because run ids are not
monotonic across re-runs of one run. Idempotency uses the Service Hook notification id (`$.id`), so Azure DevOps'
retries are duplicates.

## 4. Seeing CI failures

Builds carry `environment: ci`, so the default queue view (`production + unknown`, spec §15.5) does not show them.
Open the queue with `environment=ci` and save it as a filter, or add an entry-point link for the team. Deploy and
promote episodes carry their target environment and appear in the default view when it is `production`.

## 5. Noise controls that apply

- **Retries are occurrences**: a re-run of a failed pipeline on the same branch increments `occurrence_count`, nobody is
  notified again.
- **Flapping** (M13): a `flap.reopen_window` on the integration's lifecycle policy keeps fail–pass–fail chains in one
  episode.
- **Storm of stages / jobs**: a grouping rule such as `match: { eq: [integration.type, azure_devops] }, key:
  [resource_id, labels.run], window: 5m, min_members: 2` turns several failing stages of one run into one notification
  (M15).
- **Cancelled runs** are recorded without an episode; they prove nothing about the build.
- The explicit-recovery backstop closes an episode after 60 minutes of silence (spec §12.2). For pipelines that are
  not re-run quickly, set `auto_resolve.after_silence` on the integration's lifecycle policy to the longest gap you
  accept (for example `24h`); critical and high episodes receive the stale-state escalation before closure.

## 6. Fixtures

`tests/fixtures/azure-devops/PROVISIONAL-*.json` follow the shapes documented at
<https://learn.microsoft.com/en-us/azure/devops/service-hooks/events> and are replaced by redacted captures as soon as
the first subscriptions deliver (AGENTS.md §5.9).
