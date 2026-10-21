# ADR-15: Entra app roles are the source of roles; the local record is where they are enforced

**Status:** Accepted. Revises ADR-14 (which said authorization is "always local" without distinguishing *source* from *enforcement*).

## Context

ADR-14 put authentication behind `IIdentityProvider` and declared authorization — roles, scopes, team membership —
**always local**, so that switching identity provider changes how a user proves identity and not what they may do. That
sentence was written to protect the provider switch. Read literally it also forbids synchronising roles from Entra ID,
which is not what it was for, and which has a real cost: two lists of who is an operator, joiner/mover/leaver processes
that do not reach DeNoise, and no access review over the product.

Entra offers two carriers:

- **App roles** — declared in the app registration, assigned to users or groups on the enterprise application, delivered
  as the `roles` claim. Application-specific, visible in the assignment UI, covered by access reviews and PIM, and not
  subject to the groups overage that replaces a large `groups` claim with a pointer to Microsoft Graph.
- **Group claims** — directory groups that exist independently of this product.

Against that, three parts of this system have no Entra token at the moment they need an authorization decision:

1. **Personal access tokens.** `Principal.ForToken` computes effective permissions as the intersection of the token's
   scopes and `Rbac.PermissionsFor(user.RoleNames)` — roles read from the local user row
   (`src/DeNoise.Application/Auth/Principal.cs`). A PAT call carries no Entra token and no session, so a claim-only model
   leaves machine integrations with no source of roles at all.
2. **Service accounts and the break-glass administrator**, which deliberately do not exist in Entra — DeNoise is the
   system that reports that Entra is unreachable, so it cannot require Entra to log in while diagnosing that.
3. **Team membership and assignment**, which are operational decisions changed during a shift, not directory facts.

## Decision

Roles come **from Entra**, and are **enforced from the local record**.

| Subject | Source of truth | Carrier |
|---|---|---|
| Roles (`viewer`, `operator`, `integration_admin`, `platform_admin`) | Entra | **App roles**, `roles` claim |
| `access_scope` (MPT, PRISM, CloudIQ, PyraCloud — spec §19.2) | Entra | **Group membership**, mapped by configuration |
| Team membership, assignment, saved filters | DeNoise | Local only |
| Roles of service accounts and the break-glass administrator | DeNoise | Local only, never projected |

**Projection at login.** The OIDC callback reads `roles` and `groups` from the token and writes them onto the local user
row (upsert) inside the sign-in transaction. Authorization at request time keeps reading the local row, unchanged, so
sessions, PATs, CSRF, SSE and the RBAC matrix are untouched. The local row becomes a projection of Entra for people, and
remains the only store for the two cases Entra cannot express.

**Auto-provisioning is on.** A first login with at least one mapped app role creates the local user. The authorization
decision was already taken in Entra when the role was assigned; requiring a second, manual creation in DeNoise adds a
ticket queue without adding a control. A token with no mapped app role is refused and audited — no account is created,
so there are no ghost rows.

**Accounts flagged `local_only`** (service accounts, break-glass) are exempt from projection and keep their locally
assigned roles.

## Consequences

- One place to grant access — assigning the app role is the grant — and DeNoise appears in access reviews like any other
  enterprise application.
- **A role change takes effect at the next sign-in**, because the claim is in the token issued at sign-in and sessions
  live for the sliding 8 h / absolute 24 h of ADR-14. For *downgrades* that is too slow: offboarding and role reduction
  must also disable the account (`POST /api/v1/users/{id}/disable`) or revoke the session. This belongs in the
  offboarding runbook; removing the Entra assignment alone does not end an established session.
- PAT behaviour is unchanged and self-correcting: effective permissions are still the intersection with the owner's
  current local roles, so a projection that lowers a role immediately narrows every token that user issued.
- Group overage affects `access_scope` only. Where a tenant exceeds the claim limit the scope mapping must resolve
  through Microsoft Graph; roles never need it, which is the main reason roles ride on app roles and not on groups.
- ADR-14's "always local" now reads as *always locally enforced*; its migration note ("match existing users on email")
  stays correct for accounts that predate SSO.
- Entra becomes a dependency of granting access, but not of running: existing sessions, PATs, ingestion and notification
  continue while Entra is unavailable, and the break-glass account can still sign in.

## Alternatives considered

- **Claims-only authorization** (no local roles): rejected — PATs and service accounts have no claim to read, and every
  authorization check would need a second path.
- **Local-only roles** (ADR-14 as literally written): rejected — it keeps a second access list that no joiner/mover/leaver
  process maintains, and it was never the intent of that decision.
- **Groups for roles instead of app roles**: rejected — overage turns a role check into a Graph call, and directory groups
  are not scoped to this application, so an access review cannot answer "who can administer DeNoise".
