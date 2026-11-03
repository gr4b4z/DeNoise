# Runbook — Offboarding and role reduction

ADR-15, M11b. With SSO, roles are **assigned in Entra** (app roles on the DeNoise enterprise application) and **enforced
from the local user row**, which the sign-in projects them onto. The projection only happens at sign-in.

> **Removing the Entra assignment does not end a live session.** A session lives for the sliding 8 h / absolute 24 h of
> ADR-14, and the user keeps the roles projected at their last sign-in until it ends. Personal access tokens the user
> issued keep working too, because a PAT never touches Entra. To take access away *now*, disable the user in DeNoise.

## Leaver (access must end now)

1. In Entra, remove the user's app-role assignment (or the group that carries it). This stops the next sign-in.
2. In DeNoise, disable the account: `POST /api/v1/users/{id}/disable` (`user.manage`, platform_admin). Disabling revokes
   every session at once, and every PAT stops authenticating because the owner is disabled. It is audited as `user.disable`.
3. Check the audit log (`/audit`, target = the user) for `user.disable`, and for any `auth.login` after the time you
   removed the assignment.

## Role reduction (mover)

1. In Entra, change the app-role assignment (for example `DeNoise.IntegrationAdmin` → `DeNoise.Operator`).
2. If waiting until the next sign-in is acceptable, stop here. The next SSO sign-in writes `user.roles_projected` with the
   before and after roles, and from that moment every PAT the user issued narrows to the new roles.
3. If the reduction must take effect now, disable and then re-enable the account (`/disable`, then `/enable`). This
   revokes the live sessions; the user signs in again and the lower roles are projected. A platform_admin can also edit
   the roles directly (`PUT /api/v1/users/{id}`); the next sign-in overwrites them with the Entra assignment.

## Accounts that are not in Entra

`local_only` accounts (the bootstrap `admin` break-glass account and service accounts) are never projected and never
matched by e-mail. Their roles exist only in DeNoise: offboard them by disabling them, and rotate the break-glass
password when someone who knew it leaves (`POST /api/v1/users/{id}/reset-password`). Only a platform_admin can set or
clear `local_only` (`PUT /api/v1/users/{id}` with `localOnly`).

## Refused sign-ins

Every refused SSO sign-in is audited as `auth.oidc_refused` with a `reason`. The user only ever sees *Single sign-on did
not complete*. Common reasons:

| Reason | Meaning | Action |
|---|---|---|
| `no_mapped_role` | The token carried no app role in `Auth:Oidc:RoleMap` | Assign an app role in Entra, or check that the role values match `roleMap` (a renamed app role silently stops granting) |
| `idp_error` | Entra returned an error, e.g. `access_denied` / AADSTS50105 (user not assigned) | Assign the user to the enterprise application |
| `user_disabled` | The matched DeNoise account is disabled | Intended for leavers; enable it otherwise |
| `email_conflict` | The e-mail belongs to an account SSO may not take over (`local_only`, already linked, several case variants, or a guest token) | Link or rename the account deliberately |
| `state_*`, `code_rejected`, `nonce_mismatch` | The browser round trip was incomplete, expired, tampered with or replayed | Usually a stale tab; investigate repeats from one IP |
| `invalid_audience`, `invalid_issuer`, `invalid_signature`, `token_expired` | The id_token did not validate | Check `Auth:Oidc:ClientId` / `Authority` and the clock |
| `provider_unavailable` | Discovery, JWKS or the token endpoint could not be reached (or the managed identity for a federated credential) | Check egress to `login.microsoftonline.com`; the break-glass account still signs in |
