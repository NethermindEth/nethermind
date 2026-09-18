# Stage E audit-scaffold scope

The Stage E boundary below is retained verbatim as the accepted one-step
milestone. Stage F is a separate operational stage in this package.

## Stage F finite-fuel operational scope

This is an unaccepted Stage F candidate pending deterministic artifact
generation, direct Lean validation, and instantiation of the explicit
production adapter obligations.  The conditional theorem boundary below must
not be read as a production refinement claim before those gates pass.

Stage F translates the admitted frame-loop control order into an executable,
theorem-free Lean carrier with explicit finite fuel:

- all four dispatch table/cancellation modes, exact 1024-opcode nonterminal
  cancelable epochs (with cumulative 2048+ poll counts and one exact terminal
  count for the non-cancelable tail-call chain), successor PC bounds, and
  per-iteration fuel decrease;
- fresh and continuation preparation, full-frame precompile dispatch,
  suspension and LIFO child-parent resumption;
- success, revert, exception, CREATE deposit-invalid/deposit-OOG after child
  success (collision is delegated to the accepted CALL/CREATE route),
  top-level, and precompile failure settlement;
- state-gas reservoir/used/spill/refund merge and rollback, return data and
  bounded parent output copy, world/access/log/destroy/RIPEMD effects, tracing,
  substate/status, and cleanup/disposal.

Bytecode batches carry adapter-supplied dispatch-route evidence plus starting
PC/opcode-count anchors. The route stream has one aligned route and PC witness
per reported completed opcode, with cardinality and byte-lookup checks, and
full-precompile outcomes carry their admitted address route. This is a trace
premise consumed by the driver, not a proof of the C# handler's exact execution
order or internal transitions; the aligned list may revisit a PC for a valid
backward jump. Since a batch exposes only its
final successor, per-op successor/control transitions (including variable-width
PUSH handling) remain an adapter obligation. The driver rejects missing,
dropped, or duplicated route evidence, an inconsistent successor/count, or a
 resume whose captured parent head/call-depth stack shape is inconsistent. The
 frame carrier
intentionally excludes direct-inline STATICCALL behavior; `ExecuteCall`/CALL/
CREATE opcode leaves own that behavior, and the accepted Stage A route is a
dependency rather than CALL/CREATE source closure.

CREATE success has an explicit `createDeposit` adapter and typed outcome; the
post-child settlement must be the matching parent-continuation success or
failure marker. CREATE collision is delegated to the accepted CALL/CREATE
route, and neither is folded into a universal child-settlement callback.
Parent-resume is a separate typed settle instruction over the captured LIFO
stack shape only; PC, gas, operand stack, memory, world, and output
continuation equality remain adapter obligations. `OperationalReference.lean`
is independent of the generated module. `adapters_step_agree` derives
one-step equality from per-adapter equalities and explicit intermediate
preparation/failure/output admitted-state closure, and
`generated_runFuel_reference_runFuel` inducts over explicit fuel under
per-adapter equality and admitted-state closure. Exact route tables,
reachability, and stack-shape preservation are exposed by a separate domain
obligation theorem. The separate `ProductionAdapterObligations` structure
requires each preparation, dispatch, precompile, failure, settlement, and
cleanup adapter equality on an admitted typed domain; no universal same-leaf
production theorem is used.

## Stage F blockers

Concrete C# adapter implementations are intentionally fail-closed (`Option`;
`none` yields an incomplete result). The accepted Stage A opcode, precompile
Stage B/C, WorldJournal/FrameJournal, state-gas, and Stage D theorem identities
are hash- and `#check`-validated closure inputs only. The generated/reference
induction does not discharge those semantic obligations. Fuel adequacy for
continue and suspend/push from `gasLeft + remainingCode + parentStackDepth`,
source-to-adapter reachability,
fixed-width arithmetic, caller rollback, outer transaction finalization, and
CLR/JIT/pooled-resource behavior remain open. This stage makes no whole-EVM,
transaction, or block-processing claim.

## Declared target context and bounded boundary

The declared target context is standard-mainnet Amsterdam with
`EthereumGasPolicy`; the admitted source member remains generic over
`TGasPolicy`, and no fork/policy wiring is closed here. The source root is the
generic `ExecuteTransaction` loop. The Roslyn control anchors are, with their
recognized control/call topology and branch bindings:

- ExecuteTransaction: frame preparation, precompile/bytecode selection, return
  classification, nested child return, top-level completion, and the Failure
  route;
- RunByteCode: the dispatch-loop result boundary;
- RunDispatchLoop: exactly two call-bearing cancellation statements, one at
  entry and one at the source's post-batch polling site; nonterminal dispatch
  epochs are exactly 1024 opcodes and every completed cancelable epoch has a
  poll. Count/successor and reachability remain adapter premises, while
  malformed route vectors are rejected by the typed control validity predicates;
- FrameCleanupScope.Dispose: exceptional unwind ownership.

The generated finite-fuel plan consumes one prepared frame invocation and one
child/top-level settlement per iteration, recursing only on explicit fuel. The
source-derived topology and exact route tables supply typed bindings, while
the plan and every branch binding carry typed predicate/effect/settlement
identities and aligned actions. Those identities are lowered once from the
admitted source branch and checked against the exact Roslyn/CFG node identity
before entering the generic instruction interpreter. Leaf effects are still
adapter interfaces; this is not a C# body translation or a claim that
production adapters satisfy those interfaces.

## Open obligations

The named leaves still require individual production simulations: preparation,
bytecode and precompile execution, delegated CALL/CREATE collision routing,
post-child code-deposit accounting, gas/refund, world journal, tracing,
top-level substate, and cleanup. The Lean relation is conditional on explicit
per-leaf adapter equalities and admitted-state closure; its one-step equality is
derived from those obligations, but it is not those production simulations.
The exact source/member identities and accepted package hashes are evidence
inputs, not semantic proofs of the leaves.

The theorem starts from a preconstructed VM/frame state and excludes
`TransactionProcessor.CompleteWithoutFrame` and
`TransactionProcessor.FailContractCreate` bypass paths, intrinsic transaction
validation, caller rollback, state database behavior,
precompile native/cryptographic correctness, JIT/CLR behavior, adequate-fuel
progress, source-to-adapter state reachability, transaction processing, and
block processing remain open. The conditional finite-fuel induction is not a
global EVM or transaction/block completeness claim.
