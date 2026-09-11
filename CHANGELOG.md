# Changelog

All notable changes to Alert Hub are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer once the pilot starts.

## [Unreleased]

### Added
- **Milestone 1 — Durable ingest + queue.** `POST /ingest/{keyId}` with per-integration bearer tokens (Argon2id, key-id lookup), size and per-key rate limits, atomic raw event + `normalise` job insert and the 202/401/413/429/503 contract; `cfg.integration` versions with token rotation; `audit.entry`; PostgreSQL job queue (`FOR UPDATE SKIP LOCKED`, reservation tokens, backoff, failure queue), `JobRunner` per role, reaper, queue gauges; `seed-dev` command. Scenarios *Worker fails during processing* and *Worker fails after state commit* (job part). See `docs/milestones/M1-ingest-and-queue.md`.
- **Milestone 0 — Skeleton.** .NET 10 solution (`Domain`, `Application`, `Infrastructure`, `Contracts`, `Api`, `Ingest`, `Workers`, `Migrator` + four test projects); first migration with partitioned `alert.raw_event`, `ops.job`, `ops.outbox`, `ops.hub_component_heartbeat` and schemas `alert/ops/audit/hb/cfg`; daily partition manager; `/healthz/*` on every host; Serilog + OpenTelemetry wiring with ADR-9 metric names; `TimeProvider` injection; Docker Compose, multi-host Dockerfile, Helm chart, GitHub Actions CI. See `docs/milestones/M0-skeleton.md`.
- Handoff pack imported into `docs/` (spec v0.4, architecture and ADRs, domain model, data schema, API contract, mapping DSL, frontend spec, implementation plan, test strategy).
- `AGENTS.md` operating brief at the repository root.
