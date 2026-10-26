# Task list: soispoke v2 benchmark refresh

- [x] Reproduce and independently verify the pinned v2 profile locally.
- [x] Build and verify the local v2 plus synthetic artifact bundle; record its checksum.
- [x] Selectively integrate needed harness changes and update the active sweep matrix.
- [x] Merge fetched `origin/master` into the isolated measurement branch locally, without rewriting history. Merged base: `f080edfc4b`. Remote master is now `4295b7ffc8`, one unrelated test-only commit ahead; review/merge it before any push or dispatch.
- [x] Label the 235.8k profile rows as `single_verify_frame`; the frame limit is 225k, so these are not full-prefix throughput results.
- [x] Make first-attempt results authoritative for capacity; retain a retry only as diagnostic data.
- [x] Add privacy-conscious runner hardware capture to the workflow artifact.
- [x] Build `Nethermind.Blockchain.Test` after the merge and edits (58 projects, no warnings/errors); validate workflow YAML and shell syntax.
- [x] Re-run the v2 admission and rate-ramp smoke after the merge and latest harness edits. Both pass; all ramp rates passed on attempt 1, so the retry branch remains unexercised.
- [ ] Complete review of the full branch diff and refresh its base before any push. The two newest commits were reviewed; local HEAD `b9cfcc0b14`, remote branch `02874470c0`. No push without approval.
- [x] Earlier focused local artifact, mempool, flood, and K_retry checks passed before the latest merge/edits; see plan for exact counts.
- [x] Temporarily raise the local cap to 500k and verify high-ceiling artifact paths; restore the stock cap afterward.
- [ ] Complete the review and merge of frame-verify-gas PR #3 on `main`.
- [ ] Run both candidate builds from the merged `main` SHA, obtain exact artifact sign-off, and publish an immutable release.
- [ ] Dispatch repeated Nethermind CI runs from the pinned measurement-branch SHA.
- [ ] Recompute `R_max` from valid first-attempt rows only (`attempt=1`, `accepted=yes`), then define follow-up decisions.
- [ ] Update Findings, decision log, and Notion after the v2 CI results are verified.
- [ ] Scope Thomas's matched-gas/s, block-deadline-tail, and preemption follow-up experiments.

## Checkpoints

- [x] Checkpoint 1: local profile measurements and provenance reviewed.
- [x] Checkpoint 2: local artifact bundle and updated harness pass local verification.
- [ ] Checkpoint 3: repeated CI results are valid and reproducible.
