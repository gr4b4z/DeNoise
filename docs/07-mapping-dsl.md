# 07 — Mapping DSL, Predicates and Reference Mappings

Interpreted, declarative, no scripting. Implemented in `AlertHub.Application/Mapping`. Path expressions are **JSONPath** (RFC 9535 subset: dot/bracket member access, array index, `[*]`, filter `[?(@.k == 'v')]`). Header access via the pseudo-root `$headers`.

## 1. Mapping document

```yaml
mapping:
  version: 3                       # assigned by server on save
  identity_version: 1              # bump only when `identity:` changes
  applies_when: <predicate>        # optional; first matching mapping of the integration wins (ordered list)
  ignore_paths_for_body_key: ["$.data.essentials.firedDateTime"]   # excluded from body-hash delivery key

  fields:                          # target ← rule. Targets are the canonical event fields (spec §9).
    event_type:
      lookup:
        from: "$.data.essentials.monitorCondition"
        map: { Fired: firing, Resolved: resolved }
        default: unknown
    source_alert_id:  { path: "$.data.essentials.alertId" }
    source_event_id:  { first_nonempty: [ { path: "$.data.essentials.originAlertId" }, { path: "$.id" } ] }
    occurred_at:
      first_nonempty:
        - { path: "$.data.essentials.resolvedDateTime", when: { eq: [ "$.data.essentials.monitorCondition", "Resolved" ] } }
        - { path: "$.data.essentials.firedDateTime" }
      as: timestamp                # timestamp | string | int | float | bool | string[] | object
    severity:
      lookup: { from: "$.data.essentials.severity", map: { Sev0: critical, Sev1: high, Sev2: medium, Sev3: low, Sev4: informational }, default: unknown }
    summary:          { template: "{$.data.essentials.alertRule}: {$.data.essentials.description}" , max_length: 500 }
    resource_id:      { path: "$.data.essentials.alertTargetIDs[0]", as: string, transform: [lower] }
    resource_name:    { path: "$.data.essentials.configurationItems[0]" }
    rule_id:          { path: "$.data.essentials.alertRuleID", transform: [lower] }
    rule_name:        { path: "$.data.essentials.alertRule" }
    environment:      { lookup: { from: "$.data.essentials.alertTargetIDs[0]", regex_map: { "/subscriptions/1111-.*": production, "/subscriptions/2222-.*": staging }, default: unknown } }
    service:          { path: "$.data.customProperties.service", default: null }
    source_url:       { path: "$.data.essentials.investigationLink" }
    dimensions:       { object: { metric: { path: "$.data.alertContext.condition.allOf[0].metricName" }, ns: { path: "$.data.alertContext.condition.allOf[0].metricNamespace" } } }
    labels:           { pick: [ "$.data.essentials.monitoringService", "$.data.essentials.signalType" ], as: object }

  required: [event_type, source_alert_id, rule_id]      # missing ⇒ mapping exception, quarantined
  identity:                                             # ordered; defines fingerprint (04 §4)
    - { name: scope,       path: "$.data.essentials.alertTargetIDs[0]", transform: [lower, azure_subscription] }
    - { name: environment, field: environment }
    - { name: resource,    field: resource_id }
    - { name: rule,        field: rule_id }
    - { name: dims,        field: dimensions, optional: true }
  enrich:                                               # after fields; local lookups only
    - { set: runbook_url, lookup_table: runbooks, key: rule_id }
    - { set: service,     lookup_table: resource_service, key: resource_id, when_empty: true }
  lifecycle_profile_hint: explicit_recovery              # default for rules matched by this mapping; overridable per rule
```

### Rule kinds

| Kind | Shape | Notes |
|---|---|---|
| `path` | `{ path, default?, as?, transform? }` | missing ⇒ `default` (null if absent) |
| `const` | `{ const: value }` | |
| `first_nonempty` | `{ first_nonempty: [rule…] }` | each may carry `when:` |
| `lookup` | `{ lookup: { from, map | regex_map, default } }` | `regex_map` first match wins, `^…$` anchored |
| `template` | `{ template: "…{path}…", max_length? }` | paths in braces; missing ⇒ empty |
| `object` | `{ object: { k: rule } }` | |
| `pick` | `{ pick: [paths], as: object }` | keys = last path segment |
| `array` | `{ array: { from: "$.x[*]", each: rule, join?: ", " } }` | defined array handling |
| `coalesce_header` | `{ header: "X-Name" }` | |

`transform`: `lower`, `upper`, `trim`, `azure_subscription` (extracts `/subscriptions/{id}`), `azure_resource_group`, `hostname` (strip port), `sha256`, `truncate:N`.

`when` predicates use the grammar in §5.

### Validation on save
- All `required` targets have a rule.
- `identity` non-empty; each entry resolves to a rule or field.
- Sample payloads (`samples[]`) each produce the recorded `expected` output — stored with the version; **activation is refused if any sample fails**.
- Unknown target names, unknown rule kinds, invalid JSONPath ⇒ 400 with path to the error.

## 2. Reference mapping — Azure Monitor Common Alert Schema

Use the example above as the base. Add per `monitoringService` (`applies_when: { eq: ["$.data.essentials.monitoringService", "Log Alerts V2"] }` etc.) overrides for `dimensions`:

| monitoringService | dimensions source |
|---|---|
| Platform (metric) | `alertContext.condition.allOf[0].dimensions[*]` → `{name: value}` |
| Log Alerts V2 | `alertContext.condition.allOf[0].dimensions[*]` |
| Activity Log – Administrative | `alertContext.operationName`, `alertContext.status` |
| Resource Health | `alertContext.properties.currentHealthStatus` — treat `Available` as `resolved` |
| Service Health | `one_shot`, `informational` |

`source_version`: not provided by Azure; ordering falls back to `occurred_at` (04 §5). Canary rule payloads carry `alertRule` = `alerthub-canary-*` and are matched by an `applies_when` mapping that sets `event_type: heartbeat`.

**Fixture requirement:** at least one real Fired and one Resolved payload per monitoringService above, captured from the tenant (C5). Microsoft's published samples are acceptable as `PROVISIONAL-*` until then.

## 3. Reference mapping — MongoDB Atlas webhook

```yaml
mapping:
  identity_version: 1
  fields:
    event_type:
      lookup: { from: "$.status", map: { OPEN: firing, TRACKING: update, CLOSED: resolved, CANCELLED: cancelled }, default: unknown }
    source_alert_id:  { path: "$.id" }
    source_event_id:  { template: "{$.id}:{$.status}:{$.updated}" }     # Atlas has no per-event id
    occurred_at:      { first_nonempty: [ { path: "$.resolved" }, { path: "$.updated" }, { path: "$.created" } ], as: timestamp }
    severity:                                                            # NOT in payload — local policy
      lookup: { from: "$.eventTypeName", map: { HOST_DOWN: critical, REPLICATION_OPLOG_WINDOW_RUNNING_OUT: high, OUTSIDE_METRIC_THRESHOLD: medium, CREDIT_CARD_ABOUT_TO_EXPIRE: informational }, default: unknown }
    summary:          { template: "{$.eventTypeName} on {$.clusterName} {$.hostnameAndPort}" }
    resource_id:      { first_nonempty: [ { path: "$.hostnameAndPort", transform: [lower] }, { path: "$.clusterName", transform: [lower] } ] }
    resource_name:    { first_nonempty: [ { path: "$.clusterName" }, { path: "$.hostnameAndPort" } ] }
    rule_id:          { path: "$.alertConfigId" }
    rule_name:        { path: "$.eventTypeName" }
    environment:      { lookup: { from: "$.groupId", map: { "<prodProjectId>": production, "<stagingProjectId>": staging }, default: unknown } }
    dimensions:       { object: { metric: { path: "$.metricName" }, replicaSet: { path: "$.replicaSetName" } } }
    source_url:       { path: "$.links[?(@.rel=='http://mms.mongodb.com/alert')].href" }
  required: [event_type, source_alert_id, alertConfigId_or_eventTypeName]
  identity:
    - { name: project,  path: "$.groupId" }
    - { name: environment, field: environment }
    - { name: resource, field: resource_id }
    - { name: rule,     field: rule_id }
    - { name: dims,     field: dimensions, optional: true }
  lifecycle_profile_hint: queryable_state
```

Integration-level: `hmac.algorithm` configurable (`sha1` | `sha256`), header name configurable — Atlas' documented webhook signature has historically been HMAC-SHA1 in `X-MMS-Signature`; **verify against a captured request before enabling `hmac.required`**. Use `$headers['X-MMS-Event']` if present as a secondary `event_type` source. Severity map must be filled from the project's alert configurations (owner input, C5).

## 4. Generic webhook — recommended producer contract (documented, not required)

Producers *may* send the canonical shape directly; the default generic mapping is identity:

```json
{ "eventType":"firing", "alertId":"orders-api-5xx", "eventId":"uuid", "occurredAt":"…", "severity":"high",
  "resource":{"id":"orders-api","name":"Orders API"}, "rule":{"id":"5xx-rate","name":"5xx rate > 2%"},
  "environment":"production", "service":"orders", "summary":"…", "dimensions":{"region":"weu"}, "url":"…" }
```
Producers with other shapes get their own mapping. Heartbeat pings go to `/hb`, never through this endpoint.

## 5. Predicate grammar (routing, grouping, suppression scope, `applies_when`, `when`)

```yaml
# leaf
{ eq:  [ <ref>, <value> ] }      { neq: [...] }
{ in:  [ <ref>, [v1, v2] ] }     { nin: [...] }
{ regex: [ <ref>, "^pattern$" ] }
{ exists: <ref> }
{ gte: [ <ref>, 3 ] }  { lte: … }   (severity refs compare on the canonical ordinal)
# composite
{ all: [ p1, p2 ] }   { any: [ p1, p2 ] }   { not: p }
```
`<ref>` is either a canonical field name (`severity`, `environment`, `service`, `rule_id`, `labels.team`, `dimensions.region`, `integration.type`, `integration.id`) or, only inside mappings, a JSONPath. Routing/grouping/suppression predicates operate on the **normalised event + episode**, never on raw JSON.

Examples:
```yaml
# routing
- priority: 10
  match: { all: [ { eq: [environment, production] }, { in: [service, [marketplace, mpt-api]] } ] }
  team: team-mpt-devops
- priority: 900
  match: { eq: [severity, unknown] }
  team: team-triage
# grouping
- match: { eq: [integration.type, azure_monitor] }
  key: [service, environment]
  window: PT10M
```

## 6. Preview semantics

`POST …/mappings/{v}/preview` returns, per input event: normalised fields, `deliveryKey`, `fingerprint` + components, `identityConfidence`, matched routing rule and team, lifecycle policy resolved, and *"would create new episode / would update episode {id} / duplicate / late"* against **current** state — read-only.
