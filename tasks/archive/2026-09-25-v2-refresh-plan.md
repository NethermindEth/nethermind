# Implementation plan: soispoke v2 benchmark refresh

## Overview

Reproduce the `position-notes-v2` profile locally, package its exact verifier inputs, then update the Nethermind sweep and measure the new ceiling curve. Keep v1 results as historical data. Treat v2 as a test profile, not production-ready cryptographic material.

## Decisions

- Pin source to soispoke `v2` at `6dedda193bf09c9d80cd89b3dc23eccf580d1026` and record manifest/artifact hashes.
- Active ceilings: `100k`, `235.8k`, `250k`, `300k`, `400k`, `500k`. Retire `236.285k` and `352.8k` from the active matrix only.
- Preserve existing user work. Use isolated worktrees based on refreshed `origin/master` and `origin/main`.
- Do not force-push, close, merge, publish a release, or dispatch CI before local checks pass. Ask before external publication or dispatch.

## Tasks

### Phase 1: reproduce the profile locally

- [x] Verify the v2 manifest, source commit, test-only ceremony status, and component gas claims.
- [x] Run the pool's profile checks and relevant native/Foundry tests in a clean worktree.
- [x] Independently measure the verifier, VERIFY frame, and full validation prefix. Record commands and hashes.

**Acceptance:** Declared budget and measured burn are reported separately; the pinned source and test inputs match the manifest.

**Result:** v2 commit `6dedda193bf09c9d80cd89b3dc23eccf580d1026`; Foundry profile check and 32 tests pass; native replay passes 77 cases, 0 failures. Maximum recent-root `5,579`, VERIFY frame `210,166`, validation prefix including signature `218,545` gas.

### Checkpoint 1

- [x] Review local measurements and provenance before generating or publishing new artifacts.

### Phase 2: prepare versioned benchmark artifacts

- [x] Build the soispoke v2 and synthetic ceiling packages in an isolated `frame-verify-gas` worktree.
- [x] Verify package checksums, verifier bytecode, gas values, and package contents against the pinned profile.
- [x] Assemble the five-package local sweep bundle and checksum manifest.
- [ ] Complete human review and publish an immutable release. No release has been published.

**Local result:** The v2 verifier and four synthetic controls pass `check-verifiers.py`. `SHA256SUMS` digest: `c2a9db856dae216e38b104283cb67aed9f3b3ef00dc5a400316babbc64f8c2de`; the manifest lists each tarball's SHA-256. Source changes are in [frame-verify-gas PR #3](https://github.com/NethermindEth/frame-verify-gas/pull/3); CodeQL passes, review and merge are pending.

**Acceptance:** Release contents are reproducible from pinned inputs and include provenance and checksums. Human review and publication remain release gates.

### Phase 3: update the Nethermind sweep

- [x] Selectively integrate the needed harness changes in an isolated branch, without altering remote draft PR heads.
- [x] Update the active profile and ceiling matrix to `100k`, `235.8k`, `250k`, `300k`, `400k`, `500k`.
- [x] Keep superseded ceilings and their old results as historical evidence, not active cases.

**Local result:** The v2 case records the `235,800` sweep ceiling separately from its `225,000` VERIFY frame limit and identifies its isolated-verifier measurement scope. Synthetic controls cover 250k, 300k, 400k, and 500k. The isolated branch includes fetched `origin/master` at `f080edfc4b`; its local HEAD is `b9cfcc0b14`. Remote `test/frame-tx-v2-profile` remains at `02874470c0`, so these latest changes are not pushed.

**Additional pre-dispatch gates:** The harness labels v2 rows `measurement_scope=single_verify_frame`; do not interpret those rows as full-prefix throughput. A failed rate point may be retried for diagnosis, but only the first attempt determines capacity. The workflow now captures a privacy-conscious runner hardware profile in the run artifact. These gates were not explicit in the earlier task list.

**Acceptance:** Each active case selects the intended profile and reports ceiling, profile, and measurement scope. The workflow provenance binds the run to source SHA and released artifact digest.

### Phase 4: verify, then measure

- [x] Run focused local tests for artifact loading, gas assertions, matrix coverage, and result output.
- [x] Merge the fetched `origin/master` into the isolated measurement branch without rewriting history.
- [x] Build `Nethermind.Blockchain.Test` after the merge and harness changes (58 projects, 0 warnings or errors).
- [x] Validate workflow YAML and the embedded shell step; run `git diff --check`.
- [x] Re-run focused post-merge smoke for the v2 admission case and the v2 rate-ramp path.
- [x] Add runner hardware capture to the next measurement workflow run. No standalone hardware-only run is needed.
- [ ] After required human review, exact artifact sign-off, and publication, dispatch repeated CI runs from the pinned branch SHA.
- [ ] Recompute `R_max` from valid first-attempt rows only (`attempt=1`, `accepted=yes`), then keep equal-gas/s analysis separate.

**Local result:** Before the latest merge, both affected projects built (54 and 58 projects, 0 warnings or errors); with a temporary local 500k cap, focused mempool, flood-admission, and K_retry tests passed 5/5, 7/7, and 40/40. Artifact-loader tests passed 55/55 and release tests passed 6/6. After merging fetched `origin/master` at `f080edfc4b` and applying the latest harness changes, `Nethermind.Blockchain.Test` builds successfully (58 projects, 0 warnings or errors). Post-change smoke: the v2 admission case passed 1/1; the v2 rate-ramp case passed 1/1 on one local core and emitted `single_verify_frame` plus `capacity_basis=first_attempt`. All offered rates through 400 tx/s passed on attempt 1, so the diagnostic retry path was not exercised. This local result is not campaign throughput evidence. The branch is at `b9cfcc0b14`, 10 commits ahead of remote `test/frame-tx-v2-profile` at `02874470c0`; changes are local and not pushed. No v2 CI throughput result exists yet.

**Acceptance:** Repeated CI artifacts identify source SHA, artifact release/digest, profile, ceiling, and valid measured rows. Derive `R_max` only from valid rows with `attempt=1` and `accepted=yes`; retries are diagnostic and cannot raise capacity. No new `R_max` result exists until those runs finish.

### Phase 5: synthesize and close the campaign

- [ ] Recompute `R_max` from the repeated, valid v2 CI rows and document the runner scope.
- [ ] Update Findings, the decision log, and the Notion copy after results pass review.
- [ ] Record unresolved policy choices separately from measured findings.

**Acceptance:** The campaign deliverables agree on the pinned profile, measured curve, limitations, and open decisions.

## Risks and gates

| Risk | Mitigation |
|---|---|
| Test-only proof setup could be mistaken for production readiness | Label every artifact and result as research/test-only |
| Existing checkouts contain user changes | Work only in clean worktrees; leave dirty originals untouched |
| Old PR branches overlap or are stale | Review and integrate selectively; no force-push or base changes without approval |
| CI cannot fetch local or draft artifacts | Publish only after local verification, human review, and explicit approval |
| Remote `master` advances after the local refresh | The local merge is `f080edfc4b`; remote `master` is now `4295b7ffc8`, one unrelated test-only commit ahead (`EngineModuleTests.V2.cs`). Review/merge that delta before any push or CI dispatch. |

## Follow-up after the v2 baseline

- Thomas's matched-gas/s comparison: first derive what existing rows support, then run a controlled comparison if needed. Equal declared gas/s does not guarantee equal CPU work across shapes.
- Block-deadline tails and preemption: design a separate workload that measures slow-block headroom while spam validation competes for resources. The current median-delay ramp does not answer whether validation yields to block processing.
- Policy recommendation: use the refreshed evidence plus explicit assumptions for `B`, `r_attack`, hardware, and rate-limit scope. Do not present a single-runner benchmark as a production-safe `MAX_VERIFY_GAS` decision.
- Campaign notes and Notion: update after the v2 CI results are verified. Keep later experiments and policy questions marked as open until answered.

## Current wait state

- Waiting for Daniil's review of frame-verify-gas PR #3. No release or CI dispatch until that gate is cleared.
- Local harness updates are prepared but not pushed. Both post-merge smoke cases passed. The two newest commits were reviewed; a full branch review remains. Remote `master` has one later unrelated test-only commit, which is not merged. Refresh/review the base before any push or CI dispatch.
- After release: run a mempool smoke, then repeated `harness=both` CI measurements on one pinned SHA, with the 500k process cap. Capture hardware in those runs.
