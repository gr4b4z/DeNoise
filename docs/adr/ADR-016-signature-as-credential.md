# ADR-16: A required webhook signature may stand in for the bearer token

**Status:** Accepted (milestone 16). Extends 06 §2 ingestion authentication; does not change it for any existing integration.

## Context

Ingestion authenticates every request with a per-integration bearer token (`Authorization: Bearer …`, key id in the path,
Argon2id hash at rest; AGENTS.md §5.6). The optional HMAC check over the raw body (`cfg.integration.hmac_config`, 07 §3)
runs *after* that and proves the body was produced by whoever holds the shared secret.

GitHub webhooks cannot send an `Authorization` header or any custom header. The only thing GitHub can prove is possession of
the webhook secret, through `X-Hub-Signature-256: sha256=<hex HMAC of the raw body>`. Under the current rule every GitHub
delivery is refused with 401 before its signature is even read. Azure DevOps Service Hooks do allow custom headers and are
not affected.

Options considered:

1. **A relay in front of DeNoise** (a function that verifies GitHub's signature and adds the bearer). Moves the secret and
   the verification into a second deployable with its own availability and audit story, for one header.
2. **The token in the webhook URL** (`?token=` or a path segment). Tokens would land in GitHub's delivery log, proxies and
   access logs; AGENTS.md §5.6 forbids showing a token after creation, and this would show it on every request.
3. **Signature as credential.** When an integration's HMAC verification is configured `required: true` with a secret, a
   request *without* a bearer token is authenticated by a valid signature alone.

## Decision

Option 3, narrowly:

- `IIngestAuthenticator.ResolveSignedOnlyAsync(keyId)` returns the integration for a key id **only if** it is active, has an
  HMAC secret and `hmac.required = true`. For every other integration a missing bearer stays a 401, exactly as before.
- The ingest endpoint then runs the normal signature verification over the raw body and accepts the request **only when the
  verdict is `Valid`**; `Missing`, `Invalid` and `NotConfigured` are all 401. A bearer, when present, is verified first as
  today, and the signature rules of 07 §3 still apply on top of it.
- The verifier tolerates an `<algorithm>=` prefix in the presented value (`sha256=…`), because that is how GitHub (and
  other producers of the `X-Hub-Signature` family) format the header. Only the configured algorithm's name is stripped, so
  a `sha1=` value against a `sha256` configuration still fails.
- The key id in the URL is a routing hint, not a secret; the secret is the HMAC key, stored encrypted and never returned.
  Rotating it (`PUT /integrations/{id}` with a new `hmacSecret`) creates a new integration version like a token rotation.

## Consequences

- A GitHub integration is configured with `hmac: { header: X-Hub-Signature-256, algorithm: sha256, encoding: hex,
  required: true }` and a secret; the wizard pre-fills these for the `github` type. Its bearer token still exists (every
  integration has one) and remains valid for operators who test with `curl`.
- Replay protection is unchanged: GitHub's `X-GitHub-Delivery` is the delivery key of the reference mappings, so a
  captured and replayed request is a duplicate in the `applied_event` ledger and does nothing.
- An integration whose operator sets `required: false` to debug a signature mismatch (the verify-before-enable phase of
  07 §3) thereby also disables the signature-only path; GitHub deliveries are 401 until `required` is switched back on.
  The integration docs say so.
- `X-GitHub-Delivery`, `X-GitHub-Event` and `X-Hub-Signature-256` join the stored-header allow-list, like Atlas' headers.
