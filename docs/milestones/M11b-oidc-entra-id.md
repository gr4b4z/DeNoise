# Milestone 11b — OIDC sign-in with Entra ID

Status: **planned**, gated on **C2** (tenant, app registration, role and group mapping). Buildable any time after M4.
Behaviour: spec §19.1–19.2. Decisions: [ADR-14](../03-architecture-and-adrs.md) as revised by
[ADR-15](../adr/ADR-015-entra-app-roles-and-local-enforcement.md).

## What this is, and what it is not

The product gains a second way to **prove identity**. It does not gain a second way to **authorise a request**: the OIDC
callback ends in the same server-side session cookie that local login produces, and every downstream check — RBAC matrix,
`access_scope`, CSRF, `If-Match`, SSE, PATs — keeps reading the local user row. That is the whole point of the shape, and
it is what keeps this milestone additive rather than a rewrite of the auth surface.

**Not MSAL.js in the SPA.** The browser never holds an Entra token. Three concrete reasons, in this codebase:

1. `GET /api/v1/events/stream` is an `EventSource`, which cannot send an `Authorization` header. Today it authenticates by
   cookie. A browser-held bearer token would force the token into the query string (forbidden — it is credential material
   in a URL) or a fetch-stream polyfill.
2. `CsrfFilter` verifies a Data Protection token bound to the **session id**, and bearer requests are CSRF-exempt by
   design. Browser bearer auth means a second authentication path through every mutating endpoint.
3. Server-side sessions are revocable (`GET/DELETE /api/v1/me/sessions`). An Entra access token is not.

## Flow

Authorization code with PKCE, DeNoise as a **confidential client**:

1. `GET /auth/oidc/start?returnTo=…` → redirect to Entra with `state` and PKCE verifier in a short-lived, signed,
   `HttpOnly` cookie.
2. Entra authenticates (Conditional Access and MFA apply there, not here) and redirects to
   `GET /auth/oidc/callback?code=…&state=…`.
3. The API exchanges the code (client secret, or **workload identity federation** — preferred, nothing to rotate),
   validates the `id_token` (issuer, audience, nonce, signature against the tenant JWKS, clock skew).
4. **Projection** (ADR-15): map the `roles` claim to DeNoise roles and the `groups` claim to `access_scope`; upsert the
   local user; refuse and audit a token carrying no mapped role.
5. Create the normal session, set `denoise_session`, redirect to `returnTo` (validated as a local path).

## Configuration

```yaml
auth:
  oidc:
    enabled: true
    authority: https://login.microsoftonline.com/<tenantId>/v2.0
    clientId: <appId>
    credential: federated            # federated | secret
    callbackPath: /auth/oidc/callback
    roleClaim: roles
    roleMap:                         # Entra app role value → DeNoise role
      DeNoise.Viewer: viewer
      DeNoise.Operator: operator
      DeNoise.IntegrationAdmin: integration_admin
      DeNoise.PlatformAdmin: platform_admin
    scopeMap:                        # Entra group object id → access_scope (spec §19.2)
      "<group-guid-mpt>": mpt
      "<group-guid-prism>": prism
    autoProvision: true              # a first login with a mapped role creates the user (ADR-15)
    graphGroupLookup: false          # set when the tenant exceeds the groups claim limit (overage)
auth:
  local:
    enabled: true                    # keep for break-glass; see below
```

Helm already reserves `auth.oidc.*` (03 §4). `auth.local.enabled: false` disables password sign-in for everyone except
accounts flagged `local_only`.

## Data model — migration `ExternalIdentity`

`cfg.user` already carries `auth_provider` and `external_id` (migration `UsersAndAuth`). Added:

| Column | Type | Purpose |
|---|---|---|
| `local_only` | `boolean not null default false` | Service accounts and break-glass: never projected, password stays enabled |
| `roles_projected_at` | `timestamptz null` | When the last sign-in projected roles, for support questions |
| `external_issuer` | `text null` | Tenant issuer, so `external_id` is unique per issuer, not globally |

Unique index on `(external_issuer, external_id)` where both are not null. `cfg.scope.entra_group_ids` (present since
`UsersAndAuth`) becomes the home of the group → scope mapping when it is managed in the database rather than in config.

## Code changes

1. **New port** `IExternalIdentityProvider` in `Application/Auth/Ports.cs`. The existing `IIdentityProvider` takes
   username and password and **cannot** model OIDC — do not force it. Shape: `BuildChallenge(returnTo)` and
   `CompleteAsync(code, state, ct) → ExternalPrincipal(issuer, subject, email, displayName, roles[], groups[])`.
2. **`Infrastructure/Auth/OidcIdentityProvider`** — `Microsoft.Identity.Web` or the stock `OpenIdConnect` handler;
   justify the package in the PR (AGENTS.md §5.7). Discovery document and JWKS cached with refresh.
3. **`Application/Auth/ExternalSignInService`** — projection and provisioning in one transaction: match on
   `(issuer, subject)`, then on email for pre-SSO accounts (ADR-14 migration note), upsert roles and scopes unless
   `local_only`, write `user.provisioned` / `user.roles_projected` audit entries, create the session.
4. **Endpoints** `GET /auth/oidc/start`, `GET /auth/oidc/callback`; `GET /auth/providers` returns the real
   `OidcProvider` instead of today's hard-coded `null` — the DTO (`ProvidersResponse`) and the login page already expect it.
5. **Login page** renders the SSO button from `/auth/providers`; when `auth.local.enabled` is false the password form is
   replaced by a break-glass link, not removed (an operator must still be able to reach it knowingly).

## Entra configuration this depends on (C2)

| Item | Required value | Why |
|---|---|---|
| Supported account types | **Single tenant** | Multitenant or personal accounts means anyone with a Microsoft account authenticates |
| Enterprise application → *Assignment required* | **Yes**, with explicit user/group assignment | Without it every account in the tenant — including B2B guests — can sign in |
| App roles | Four, matching `roleMap` | The grant and the gate become one action; access reviews see them |
| Group claims | Emitted, or `graphGroupLookup` enabled | `access_scope` per spec §19.2 |
| Redirect URI | `https://<publicBaseUrl>/auth/oidc/callback` | Web platform, not SPA |
| Credential | Federated credential preferred | Nothing to rotate, no secret at rest |

## Tests

`tests/DeNoise.Integration.Tests/Milestone11b/OidcScenarios.cs` against a mock OIDC provider (Docker Compose already
reserves one, 09 M0):

| Scenario | Required result |
|---|---|
| First sign-in with a mapped app role | User created, roles and scopes projected, session issued, audited |
| Sign-in with no mapped role | Refused and audited; **no** user row created |
| Role removed in Entra, user signs in again | Projection lowers the role; every PAT that user issued narrows on the next call |
| Role removed in Entra, session still live | Still authorised until disable or revoke — the gap ADR-15 names, asserted so it cannot regress silently |
| Pre-SSO account matched on email | Keeps its identifier, history and PATs; `auth_provider` becomes `oidc` |
| `local_only` account | Not projected; password sign-in still works with `auth.local.enabled: false` |
| Tampered `state`, replayed `code`, wrong audience, expired token | Each refused distinctly in the audit, identically to the caller |
| `returnTo` pointing off-host | Ignored; redirect goes to the queue |
| *User lacks resource access* (spec §22) | Re-run under OIDC: unchanged, because enforcement did not move |

Unit: role and group mapping, overage path, `returnTo` validation. Playwright: the SSO button appears when the provider
is configured and the break-glass link when local login is disabled.

## Risks

- **Entra becomes a dependency of granting access.** It is not a dependency of running: sessions, PATs, ingestion and
  notification continue during an outage, and the break-glass account signs in. Keeping that account is a requirement of
  this milestone, not an optional hardening step.
- **Downgrade latency.** Covered above and asserted in tests; the offboarding runbook must say that removing the Entra
  assignment does not end a live session.
- **Mapping drift.** `roleMap` and `scopeMap` are configuration: a renamed app role silently stops granting. The health
  surface should show the mapping and the last successful projection.

## Definition of done

Scenarios above green against the mock provider; a real sign-in verified against the tenant from C2; OpenAPI regenerated
(`/auth/providers` response shape, two new endpoints) and contract tests pass; `CHANGELOG.md` entry; Helm values
documented; offboarding runbook updated; ADR-15 referenced from the PR.

## Effort

Backend ~4 days, UI ~1 day, tests ~2 days, plus the tenant round-trip for C2.
