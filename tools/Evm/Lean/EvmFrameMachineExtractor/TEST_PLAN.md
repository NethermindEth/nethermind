# Verification and mutation plan

The independently accepted Stage A routing/dependency boundary must continue to pass warning-as-error extractor/test builds, Microsoft.Testing.Platform, the package Lake build, direct warning-as-error Lean jobs, and byte-identical two-run extraction after every boundary change. The mutation suite covers each serialized field shape, scalar-array shape, all 174 external source/dependency inputs, exact member/signature and production-table assignments, dependency routing IR and proof modules, artifact digests, and emitter independence. Later frame execution and adapter items below remain a plan, not a passed claim.

## Extractor fail-closed tests

- The design IR has exactly 1024 table-major ordered routes with table `i / 256` and byte `i % 256` at index `i`.
- Stage A admission rejects pending acceptance, every null/invalid source or syntax digest, every unresolved member selector or route, and every unadmitted opcode package or table-byte route. Precompile wrapper admission is intentionally false and required to remain so until its later theorem exists.
- Omitted, null, duplicated, renamed, case-changed, reordered, unknown, and noncanonical fields fail deserialization.
- Every source byte mutation, canonical syntax mutation, member syntax mutation, package manifest mutation, and raw build-selector mutation fails.
- Coordinated digest mutations fail because digests are recomputed from source/artifact bytes rather than merely shape-checked.
- The source manifest has exactly 32 table-ordered dependencies: the 14 named opcode packages followed by the 18 named precompile wrappers; every path is globally unique across both groups, and duplicate names, duplicate paths, or swapped identities fail.
- Regeneration in two clean output directories is byte-identical for IR, manifest, and Lean.

## Route and composition matrix

- All 1024 table/byte pairs across the four tracing/cancellation tables; lookup must include `Frame.dispatchTable`.
- All instruction-tracing, cancellation, and action-tracing capability combinations; action failure callbacks must be absent when action tracing is disabled, while operation failure callbacks follow only the instruction-tracing table bit.
- Amsterdam-enabled and unassigned/bad-instruction routes; the accepted Stage A artifact has zero fork-disabled Amsterdam routes.
- Exact `GetHandlers` table/flag selection plus production assignment LHS/RHS lowering for each of the four table substitutions; repinned-semantic swaps, duplicate active assignments, RHS changes, EIP predicate inversions, compound or conditional fork assignments, and inherited fork-flag mutations must fail even when whole-file digest rejection is bypassed.
- Exact sibling proof modules and per-package required theorem sets; generated imports and `#check`s must compile for all 16 adequate operational theorem identities, including the Environment and CallCreate package-wide Amsterdam theorems, while module-content, theorem-name, signature, omission, reordering, or duplication mutations fail closed.
- Duplicate owner, missing owner, wrong package theorem, wrong handler root, wrong activation, and swapped byte mutations.
- Immediate continue, terminal stop, explicit revert, exceptional halt, child suspend, nested child suspend, and resume.
- CALL/CREATE success, revert, exception, collision, internal invalid/OOG code-deposit failure, nested non-direct precompile soft failure, direct precompile soft failure, and precompile hard failure.
- Parent/child gas merge mutations for execution gas, reservoir, spill, state used, refund, and repayment order.
- Settlement discriminant mutations for execution type, physical create preexistence, CREATE and NEW_ACCOUNT charge flags, advanced state-gas refunds, child snapshot, and transaction-wide RIPEMD touch restoration.
- Success, revert, exception, and failed code-deposit order mutations must independently constrain `Refund`, `UpdateGasUp`, advanced-refund removal, child-state restoration, halt restoration, create/new-account credit, snapshot rollback, physical-new-account deletion, commit, and spill repayment. In particular, swapping code-deposit halt restoration, CREATE credit, or advanced-refund removal must fail.
- Failure-origin mutations must distinguish `HandleException` trace-before-restore from `HandleFailure` restore-before-operation/action-trace, and must reject nested precompile clear-gas after classification or an exceptional-halt merge for its soft-failure route.
- World/access/log/destroy/returndata commit versus rollback mutations.
- Top-level closure must consume the terminal `FrameResult.frame`, not the pre-step machine frame.
- Empty code, end-of-code, invalid instruction, cancellation, and each trace mode.

## Precompile matrix

- Every provider address, a non-precompile address, a leading-zero spelling mutation, duplicate address, wrong activation, wrong gas debit, native failure, out of gas, and success.
- Each accepted wrapper must be mutation-sensitive to input decoding, gas calculation, output, and success/failure status.
- Cryptographic correctness remains outside this package unless its wrapper imports a separately accepted algorithm theorem.

## Lean gates

- Compile state, independent execution, generated kernel, refinement, and transaction adapter as separate jobs.
- Stage A proves generated equality for bounded route lookup and precompile lookup. Settlement by every exit kind, step, drive, and the closed Amsterdam entrypoint remain later gates.
- Prove the successful immediate route projection agrees with every selected opcode-package theorem.
- Prove the generated production-input projection is total on admitted message-call/create EVM entries and uniquely determines the top-level phase, frame baselines, pre-value snapshot, zero initial `VmState.Refund`, and transaction-world relation; arbitrary caller-supplied initial machines are not admissible.
- Prove the concrete completed-result projection preserves `executionControlOf`, independently computes the requested directive, uses nonnegative `VmState.Refund` equal to the opcode refund counter, carries the RIPEMD latch, and consumes it after outer rollback.
- Require an actually completed run for every admitted adapter execution; prove unbounded fuel adequacy or retain completed fuel adequacy as an explicit theorem parameter. Incomplete fuel cannot be installed as a successful oracle result.

## Independence defenses

- Before the semantic-IR emitter exists, every emission attempt fails without writing bytes and the emitter source contains no executable transition template.
- Generated Lean may eventually import `FrameMachineState` only; it may not import `FrameMachineExecution`, refinement modules, `TransactionReference.Oracle.executeEvmCall`, or sibling handwritten transitions.
- Reject generated `theorem`, `axiom`, `sorry`, `admit`, and proof-body tokens.
- Mutating any admitted semantic IR route, exit-merge rule, or precompile route must change the generated executable bytes and break a refinement or mutation test.
- A normalized-text comparison must reject copying the independent `routeAt`, `settleChild`, `stepFrame`, or `drive` body into an emitter constant/template.
