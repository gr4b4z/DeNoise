# Operator usability session script

Spec §22: "Release readiness requires processing correctness **and** successful operator completion of the core UI
workflows." Run this with two operators of the pilot team, 45 minutes each, on a staging stack with realistic data
(a shadowed integration is ideal). Observe, do not guide. Record time to complete, hesitation points and whether
the operator could say *why* the hub did what it did.

## Setup

- Operator account with the `operator` role and the team's scope; a second browser tab for the facilitator on `/audit`.
- Seed: ~30 open episodes across severities, one group, one suppression window ending in 30 minutes, one integration
  with a mapping failure, one heartbeat late.

## Tasks (say the task, not the screen)

| # | Task | Pass when | Screen(s) exercised |
|---|---|---|---|
| 1 | "Find what your team needs to look at right now and take the most urgent one." | Opens *Needs attention*, acknowledges a critical/high episode within 2 minutes | queue views, ack, keyboard triage |
| 2 | "Tell me why this alert is in your queue and who else is affected." | Reads the explanation panel and the routing section aloud correctly; finds the group members | episode detail, explanation, group |
| 3 | "This one is a known issue for the next two hours — make it stop paging without losing it." | Silences the episode (not close) with a reason | silence action |
| 4 | "Orders is being deployed tonight 22:00–23:00 Warsaw time; nobody should be notified about it." | Creates a maintenance window with scope `service = orders`, wall-clock times, the zone and a reason | suppressions |
| 5 | "Did anything close on its own that should not have?" | Uses history with `evidence = inactivity_unverified`, opens one, can explain the evidence | history, closure section |
| 6 | "Is the source for integration X healthy?" | Reads coverage state, last signal and the mapping failure; knows what a failure means | integrations, health, failures |
| 7 | "Hand this alert to the platform team with a note." | Assigns to the other team with a note | assign, note |
| 8 | "Something is wrong with the hub itself — where would you look?" | Opens `/hub`, reads components and queues | hub health |
| 9 | "Save your view so you find it tomorrow." | Saves a filter and finds it in the rail | saved filters |

## Debrief questions

1. Which screen did you not trust, and why?
2. Where did you need to read raw JSON to decide?
3. What would you have done in the legacy tool that you could not do here?
4. Did any automatic decision (routing, grouping, auto-resolve) surprise you? Could you find the reason?

## Recording

| Task | Operator A time | Operator B time | Completed | Notes |
|---|---|---|---|---|
| 1–9 | | | | |

A task fails the session if either operator cannot complete it without help. Failures block the pilot start
(fix and re-run the failed tasks only).
