# DeNoise — Coding Agent Handoff Pack

**Prepared:** 11 September 2026 · **Owner:** Sylwester Grabowski, Shared Services, SoftwareOne
**Target stack:** .NET 10 backend, React 19 frontend, PostgreSQL, AKS

## Contents

| File | What it is | Read when |
|---|---|---|
| `spec/denoise-spec-v0.4.md` | Product and architecture specification — the behavioural source of truth | Always available; consult per milestone |
| `01-requirements-completeness-review.md` | Gap analysis of the spec against implementability; decisions taken; **10 open owner questions (C1–C10)** | First — owner and agent both |
| `02-AGENTS.md` | Agent operating brief: stack, repo layout, rules, definition of done. **Copy to repo root.** | Before any code |
| `03-architecture-and-adrs.md` | Component design, request paths, 13 ADRs, Helm values, metric names | Milestone 0 |
| `04-domain-model-and-state-machines.md` | Aggregates, episode/coverage/heartbeat state machines, dedup and fingerprint algorithms, ordering, auto-resolve guards, policies, RBAC matrix, timer catalogue | Milestones 2, 5, 6 |
| `05-data-schema.md` | PostgreSQL schema with the invariants that must be constraints (★), retention jobs | Milestone 0–2 |
| `06-api-contract.md` | Ingestion, heartbeat ping, application API, SSE, auth, error model, notification payloads | Milestone 1, 4, 6, 7 |
| `07-mapping-dsl.md` | Mapping DSL, predicate grammar, reference mappings for Azure Monitor CAS and Atlas, generic webhook contract | Milestone 2, 8 |
| `08-frontend-spec.md` | **Design direction (§0)**, routes, screens incl. login and user admin, components, state, realtime, a11y | Milestone 4 onward |
| `09-implementation-plan.md` | 13 vertical slices with acceptance scenarios and owner dependencies per slice | Planning; start of each milestone |
| `10-test-strategy.md` | Test layers, scenario→test mapping, fixtures policy, property/load/security tests, release checklist | Every milestone |

## How to start

1. Owner: answer C5 (real payload captures) before Milestone 8. C1 is resolved (design direction in 08 §0); C2 (Entra ID) is deferred — v1 authenticates with local username/password (ADR-14), OIDC is milestone 11b.
2. Agent: read `02-AGENTS.md`, then `09`, then begin Milestone 0. Milestones 0–4 need no owner input.
3. Every PR: one vertical slice, scenarios green, OpenAPI regenerated, decisions listed.

## Conventions used in the pack

- **D / O / N** in `01`: decided here / open for owner / agent may choose.
- **★** in `05`: database invariant, not negotiable.
- Spec section references use `§`.
- All values marked "initial" or "proposed" are placeholders to be replaced from the measured baseline (spec §3.3, question C9).

## Status of the specification

v0.4 is **for decision**: the build-versus-adopt gate in spec §6 has not been closed. This pack lets an agent start the foundation milestones, which are cheap and reversible; committing beyond Milestone 3 should follow the gate decision.
