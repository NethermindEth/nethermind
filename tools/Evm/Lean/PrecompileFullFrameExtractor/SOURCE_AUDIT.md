# Exact source profile and proof boundary

`Admission.json` fixes the production bytes and the dependency bytes. It is embedded in the extractor, and the external copy must be byte-identical. This package has no automatic rebaseline or accept-current-source option.

| Production member | Admitted fact |
|---|---|
| `ExecuteTransaction<TTracingInst>` | Precompile dispatch, failure label, outer exception filter, top/nested split, successful and reverted settlement order |
| `ExecutePrecompile` | Action address, transfer-log-before-Run, returned-failure hard mapping, top managed failure, nested gas clear ownership |
| `RunPrecompile<Eip158>` and `RunPrecompileCore<Eip158>` | Amsterdam specialization, local gas copy, executing-account touch, RIPEMD latch, pricing-before-Run, success-only gas installation |
| `ExecutePrecompileCall`, `GetErrorString`, `CallResult` | Leaf success/failure mapping, nullable error formatting, managed catch, native exit exclusion, exception discriminator |
| `VmState.To`, `GetBalanceCredit`, `CreditsBalance` | Code-source action address versus executing-account balance address and call-kind credit rule |
| `HandleFailure`, `PopAndRestoreParentState` | Snapshot/RIPEMD restore before failure tracing; top substate versus nested state-gas restoration |
| `HandleRegularReturn`, `HandleRevert`, `CommitToParent` | Return/resume fields, snapshot handling and commit position |
| `IncorporateChildStateGasRefunds`, `RemoveAdvancedStateGasRefund` | Advanced-refund reconciliation positions |
| Ethereum gas policy, pricing kernel, state-gas adapter/kernel | Concrete helper identities and separately pinned arithmetic refinements |

Each selected member carries a Roslyn token hash and an ordered list of top-level statement hashes. The enclosing exact file hash also rejects changes outside those members and all preprocessor changes. The accepted Stage A triple supplies the remaining standard build, resolver, cache, activation and leaf-source closure; extraction rechecks every source/dependency byte listed there.

The lowering is intentionally a reviewed, exact-statement profile. Its theorem-free template is admitted only for these exact source files; it is not a generic CFG translator. The reference has a separate select/project architecture and cannot import the generated module. The target frame-machine settlement is never imported into generated code. A change to either the independent reference or template fails the embedded admission check until the profile is deliberately reviewed and rebuilt.

`source_execute_precompile_refines_reference` checks all typed prices, leaf outcomes and raw control routes. `generated_execute_is_raw_admitted` derives the raw settlement domain from `Entry.Admitted`, generated execution and the narrow front-callback parent-stack contract; it does not accept a caller-supplied post-execution route proof. `settlement_refines_target` checks actual sequencing over arbitrary settlement primitives, so it does not assume that clearing twice is idempotent. `source_full_frame_refines_reference` composes those facts under per-field front agreement, oracle agreement, the admitted precompile boundary, front parent-stack preservation and top-adapter shape contracts. `pricing_refines_wrapper` applies the existing UInt64-bounded pricing theorem.

The primitive record is shared between generated settlement and its target. Its correspondence to production state/world/tracer APIs remains an explicit adapter premise. Exact dependency hashes and theorem names are admission evidence, not a proof that those adapters have been composed with concrete WorldJournal or the full frame driver.
