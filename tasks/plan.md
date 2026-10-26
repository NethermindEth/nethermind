# MAX_VERIFY_GAS campaign: plan

Updated 2026-10-05. Supersedes `archive/2026-09-28-v2-plan.md`.

## Goal
- Defensible MAX_VERIFY_GAS for EF: lower bound from real apps, upper bound from node harm per hardware tier.
- Draft answer: 300k, range 236k-300k. v5 confirms or moves it.

## Where we are
- v4 (20 runs, mixed SHAs, pre-#14085) is done and published. Group got a status message today (Slack 2a).
- Decision today: **v5 = one coherent rerun** on current master with the reviewed harness. v5 replaces v4 everywhere. No cross-version comparison.

## What changed today
- **#14135 (harness):** Lukasz's review fixed (honest rate/lag checks, DI wiring, buffer wrapper). DI refactor leaves counts identical where comparable. Master merged. Workflow runs in parallel and on the Builder runner. Needs re-approval.
- **#14085 merged:** build-path preemption is now in master, so v5 producer numbers include it.
- **#14265 (new, draft):** signature verification yields to block import and building. Measured "reduced": residual 0.7-6 ms to ≤ 0.3 ms. Claude review: first signature never preempted (fixed). Second review pending.

## v5 runs (pre-registered, `v5 pre-registration` in vault)
| Arm | Machines | Suites | Reps | Status |
|---|---|---|---|---|
| Main | Mid, Low (GCE) | cost, producer, import, honest | 2 | wave 1 running, wave 2 next |
| Main | Builder (bare metal) | producer, import, honest, cost | 2 | 1 running, 7 queued |
| #14265 | Mid, Low | import, producer | 2 | wave 3, after wave 2 |

- GCE waves of 8 VMs (quota holds). ~3.5 h for GCE. Builder overnight.
- Stop at the first failed run; never re-dispatch after a cancel.

## Tomorrow
1. Analyse v5 with frozen `analyze_suites.py`.
2. Results, Notion v5, artifact v5, Slack final message (2b) with protected vs direct line.
3. #14265 result as a separate line ("with #14265, open").

## Open questions (others)
- Daniil: which tier judges the build path (full node or attester / local builder)?
- B, k, and whether this goes to the EIP authors.
- Fair per-head budget design.

## Rules
- No CI dispatch, push, Slack or publish without Manuel's ok.
- No analysis rule changed after reading data.
- Machine names everywhere: Builder, Mid, Low, Full node.
