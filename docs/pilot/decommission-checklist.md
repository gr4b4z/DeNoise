# Decommission checklist — per legacy alert route

Spec §7.3 point 2: the hub does not become the sole delivery path for `critical`/`high` until a rotation capability
exists. Decommission a legacy route only when every line below is true for **that route**; keep the evidence link.

Route: ______________________ (source → channel/person)   Owner: ______________   Date: __________

| # | Condition | Evidence | ✓ |
|---|---|---|---|
| 1 | Every condition the route carried is mapped in the hub (mapping preview on captured payloads, 07 §6) | Integration → Mappings, sample set from **C5** | |
| 2 | Eight pilot weeks with the route and the hub live in parallel | pilot runbook weekly table | |
| 3 | No condition was notified by the route and missed by the hub during those weeks | comparison log; hub delivery attempts for the same instants | |
| 4 | The hub's destination for the route's audience exists, has a fallback and passed *Send test* in the last 30 days | Destinations → deliveries | |
| 5 | Severity `critical`/`high` on this route: an out-of-hours rotation target is configured and verified (**C7**), or the route is explicitly kept for those severities | escalation policy step; *Out-of-hours critical alert* scenario record | |
| 6 | Coverage for the source is configured (canary, heartbeat or API probe) and has been `ok` for 14 days | Integration health | |
| 7 | Divergence report within threshold on the last three runs (or manual spot-check for sources without a state API) | Integration → Health → Divergence | |
| 8 | The team completed the usability session | `docs/pilot/usability-session-script.md` record | |
| 9 | Runbook links and source links render on the hub's episodes for this route | episode detail → Links | |
| 10 | The legacy route's owner has agreed a rollback path (re-enable within 15 minutes) and the date it expires | change record | |

Decommission = disable the legacy route, keep its configuration for 30 days, add the route to the hub's shadow
comparison for one more week (turn shadow **on** for a duplicate integration if needed), then delete.
