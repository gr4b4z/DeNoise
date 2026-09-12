# Milestone 7 — Webhook templates + destination management (backend)

Status: **backend done**; the 08 §3.7b screens (destinations, templates editor with live preview) follow in the UI half of this milestone.

## What exists

| Area | Where | Notes |
|---|---|---|
| Model | `Domain/Notifications/WebhookTemplate.cs`, migration `WebhookTemplates` | `cfg.webhook_template` as in 05 §4: `(template_id, version)` key, `name`, `format` (`json` \| `text`), `content_type`, `body`, `description`, `builtin`, `sample_event`, `sample_output`, `created_by/at`, `activated_at`, `deactivated_at`. Exactly one active version per template id (partial unique index); built-ins are read-only and never deactivated. |
| Renderer | `Application/Notifications/Templates/TemplateRenderer.cs` | Liquid via **Fluid.Core** (new package, Application project — the only maintained .NET Liquid engine with a sandboxed `TemplateOptions`; no reflection on CLR members is possible because the model is handed over as plain dictionaries, lists and primitives). Default filters only (none touch I/O or the clock), 200 ms render budget, 256 KiB output cap, `MaxSteps` 20 000 / `MaxRecursion` 32. `json` format encodes every interpolated value with the JSON escaper and the output must parse as JSON, so payload text (quotes, `</script>`, `{{ }}`) can never break the document; `text` format renders verbatim with `escape` available for HTML receivers. Parsed templates are cached per `(template id, version)`. |
| Built-ins | `BuiltInTemplates.cs`, `TemplateService.EnsureBuiltInsAsync` (run by the API host at startup) | `generic-json` (…f001: the model itself, sent as-is — no rendering), `teams-adaptive-card` (…f002: Adaptive Card 1.4 in a Workflows envelope, "Open in Alert Hub" action), `slack-blocks` (…f003), `plain-text` (…f004, `text/plain`). Seeded idempotently; a fresh install has four templates before the first destination exists. |
| Model for templates | `NotificationModel.cs`, `OutboxModel.Of` | The outbox payload (04 §6) plus `event`, `episode.url` (`Notifications:PublicBaseUrl`), `hub.name`, `now`; `NotificationModel.Sample(now, eventType)` is the preview sample when the operator supplies none. |
| Versioning | `TemplateService` | `CreateVersionAsync` parses, validates the format and renders the sample **before** the row is written (a template that cannot render its own sample is refused with 400 and paths); versions are inactive until `ActivateAsync`, which deactivates the previous active one in the same transaction (audit `template.activated`). Built-ins reject new versions and activation changes. `RenderPreviewAsync(id, version, sample?, episodeId?)` renders a stored version against a supplied sample or a real episode; `RenderDraftAsync` renders an unsaved body for the editor. |
| Dispatcher | `OutboxDispatcher` | Webhook destinations with `body_template_id` render the active version of that template right before the send and use the template's `content_type` (`ResolvedDestination.ContentType`, honoured by `WebhookChannel`). A template that fails to render (missing active version, timeout, invalid JSON, cap) is a **permanent** failure: `ops.delivery_attempt` row with outcome `permanent` and the error, outbox `failed`, `hub.delivery_failure` to the fallback with the generic body — the Hub never guesses a body. E-mail destinations keep the model; the channel composes the mail. |
| Destinations | `DestinationService`, `Api/Endpoints/ConfigEndpoints.cs` | `PUT /api/v1/destinations/{id}` with `If-Match` (428/409): name, team, fallback (≠ self, must exist and be active), URL (re-encrypted; insecure URLs still refused unless `Notifications:AllowInsecureDestinations`), method, headers (whole map replaced, encrypted), `rotateSigningSecret` (new secret returned **once** in the response), timeout, event types (validated against `NotificationTypes.All`), e-mail recipients, `bodyTemplateId` / `clearBodyTemplate`, `active`. `POST /{id}/test` sends `destination.test` through the real channel with the destination's template and reports outcome, HTTP status, latency, error, response excerpt and the rendered body; the attempt is recorded and the destination's health counters updated. `POST /{id}/reveal { password }` returns the URL and header values after the session user re-types their password (PATs cannot reveal; 403 otherwise). `GET /{id}/deliveries?limit=` lists the latest delivery attempts. `DestinationSummary` now carries `bodyTemplateId`, `timeout`, `hasHeaders`, `hasSigningSecret`. |
| Template API | `Api/Endpoints/TemplateEndpoints.cs` | `GET /api/v1/webhook-templates` (active versions), `GET /{id}` (all versions), `GET /{id}/{version}`, `POST` (new template or new version of an existing id, 201 / 400 with validation paths / 409 for built-ins), `POST /render` (draft preview), `POST /{id}/{version}/render { sampleEvent?, episodeId? }`, `POST /{id}/{version}/activate` (204). Reads need `episode.read`, mutations `destination.manage` + CSRF. |

## Tests

| What | Test |
|---|---|
| JSON escaping of hostile payload text, text format verbatim + `escape`, only the model is reachable (no CLR members, no `{{ model.GetType }}`), invalid JSON output and oversized output rejected, parse errors reported with positions, every built-in renders the sample to its advertised shape | `Application.Tests/Notifications/TemplateRendererTests.cs` |
| Teams destination receives an Adaptive Card whose text is a JSON value (quotes in the resource name intact), fallback receives the generic model | `Milestone7/TemplateScenarios.Destination_with_the_teams_template_*` |
| New version inactive until activated, sample output stored, preview renders, activation switches the body sent; broken version refused at creation; stale `If-Match` on the destination is a conflict | `Template_versions_render_preview_and_activation_*` |
| Test send goes through the channel with the rendered body and reports the outcome | `Test_send_goes_through_the_channel_*` |
| Spec §22 *Webhook destination fails permanently* through the template path: no active version ⇒ permanent attempt, outbox failed with the reason, `hub.delivery_failure` to the fallback, nothing sent with a guessed body | `Scenario_WebhookDestinationFailsPermanently_via_a_template_*` |

## Decisions made

- **Render at send time, not at enqueue time.** The outbox keeps the model; the body is rendered by the dispatcher with the version active at that moment, so activating a fixed template repairs deliveries still in the retry queue.
- **`generic-json` is not a template.** It is the model serialised by the Hub, so it can never fail to render and stays the mandatory fallback body (ADR-7).
- **Render failure is permanent, not retryable.** Retrying cannot fix a template; the fallback and `hub.delivery_failure` make the problem visible where the on-call looks.
- **Built-ins are immutable.** Operators copy a built-in into a new template instead of editing it, so an upgrade can ship a fixed built-in without merging.
- **Reveal needs the password again** (08 §3.7b) and is session-only, so a leaked PAT cannot dump webhook URLs.

## Not in this half

The React screens (destination list and form with template picker + live preview via `POST /webhook-templates/render`, test send panel, reveal dialog, deliveries tab; template list, versioned editor with preview and activate) — next commit.
