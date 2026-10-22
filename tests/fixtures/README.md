# Fixtures

Payload samples used by tests (AGENTS.md §5 rule 9). Every `<name>.json` has a sibling `<name>.source` naming where it
came from: a redacted capture ("captured from <tenant/project>, <date>, redacted by <who>") or a vendor documentation URL.
Vendor-doc samples are prefixed `PROVISIONAL-` until replaced by real captures (owner question C5).

| Integration | Status |
|---|---|
| `generic-webhook/` | Contract examples from `docs/07-mapping-dsl.md` §4 (this repository is the producer contract's source of truth). |
| `azure-monitor/` | Not yet present — needs C5 captures or Microsoft Common Alert Schema samples marked `PROVISIONAL-`. |
| `atlas/` | Not yet present — needs C5 captures. |
| `azure-devops/` | `PROVISIONAL-*` from the Service Hooks event reference (`build.complete`, run / stage state changed, release deployment completed); same bytes as the `ReferenceMappings.AzureDevops*Sample` constants. |
| `github/` | `PROVISIONAL-*` from the GitHub webhook events reference (`workflow_run`, `workflow_job`, `deployment_status`, `deployment`, `ping`); same bytes as the `ReferenceMappings.Github*Sample` constants. |
