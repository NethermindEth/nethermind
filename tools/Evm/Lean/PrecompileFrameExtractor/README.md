# Precompile frame extractor — Stages A and B

This package is an isolated, standard-build-only extractor for the Amsterdam
precompile registry and the bounded frame boundary around it. It emits typed
IR, a strict source/dependency manifest, and theorem-free Lean definitions for
registry activation, cache resolution, route selection, pricing, result
classification, and outcome-dependent frame residue.

The package does not refine production cryptography, native bindings, the
WorldJournal, transaction reachability, or CLR/process behavior. Each of the
18 leaf computations is represented by an exact, hashed handwritten
`tools/Evm/Lean/Eip803x/Precompiles/*.lean` oracle identity. The oracle is an
assumption, not an emitted theorem. Each identity is also tied to an exact
production helper/member-access mapping in the closed Roslyn source; changing
that helper is rejected. At the pinned source state the manifest contains 122
closed sources and 154 exact member identities, including the admitted
`EvmPooledMemory.TrySave` overload.

The standard route is `CALL`/`CALLCODE`/`DELEGATECALL`/`STATICCALL` through a
full child frame, except for the standard direct `STATICCALL` optimization.
Direct execution is rejected for instruction/action tracing and RIPEMD-160.
`CALL` and `STATICCALL` resolve `codeSource`; `CALLCODE` and `DELEGATECALL`
execute against `env.ExecutingAccount`. The emitted cancellation facts include
separate `cancelledBeforeDispatch` and `cancelledAtBoundary` inputs, plus the
successful-batch and successor bounds needed for the production boundary
predicate. The 1024-opcode condition is only boundary eligibility; it is not
cancellation without the second input (and the cancelable flag gates both
polls). The direct inline order is `price -> Run -> account touch -> refund
child gas -> returndata -> output copy -> stack success`; pricing failure is
`state-gas restore -> returndata clear -> stack failure`, and leaf failure is
`execution-gas clear -> state-gas restore -> returndata clear -> stack
failure` (output-copy OOG returns `OutOfGas`). The typed `outputCopyOutOfGas` residue is
`price -> Run -> account touch -> refund child gas -> returndata -> return
OutOfGas`; the failed `TrySave` performs no output write and no stack push.
That no-write claim is admitted by the exact Roslyn control-flow shape of
`EvmPooledMemory.TrySave`: its `isViolation` false-return branch is before
`UpdateSize`, `SaveAfterGas`, and any memory mutation.
Direct execution has no
snapshot/commit or `HandleRegularReturn`/`HandleRevert` path. Full-frame action
tracing is conditional.

Full-frame residues distinguish pricing hard failure, returned leaf hard
failure, managed nested exception/soft revert, and success. The admitted order
is AST-bound: hard failure restores the snapshot, clears return data, then
restores parent state gas; managed nested failure clears execution gas, restores
parent state gas, then performs `HandleRevert` (snapshot and returned returndata);
success refunds child gas, handles the return/returndata, then commits. The
direct route intentionally keeps its two leaf-failure classes at the same
production residue.

The cache model includes `SupportsCaching`, partition availability, normalized
input/address/reference-equal-spec keys, hit/miss/invalid-input branches, the
original-input run argument, and the post-run cache-update rule. The typed world
callback relation is explicitly open (`compositionGateOpen = true`) until the
separate WorldJournal extractor is composed; this package does not claim that
composition or transaction reachability.

The extractor fails closed on ZK source selection, missing source or dependency
hashes, unknown provider/cache decorators, wildcard provider discovery,
unresolved native/helper identities, changed route order, changed address,
fork-ancestry assignment, cache, call-target, cancellation, result bindings,
or a moved/modified `TrySave` bounds-failure branch. The generated IR records
that exact source/member admission; it remains a source-shape guard, not a
proof of CLR, pooling, or native execution.

## Stage B operational refinement

Stage B retains the accepted Stage A artifacts unchanged and adds a bounded
operational transition for the standard inline `STATICCALL` precompile path.
The generated kernel covers direct-route decline, cancellation before dispatch
and at an eligible successful boundary, defensive input-memory OOG, pricing
overflow and ordinary OOG, returned leaf failure, managed leaf exception,
success, and output-copy OOG. It records execution/state-gas child creation,
halt restoration or success refund, returndata, clipped output, memory-write,
stack-result, account-touch, and ordered-effect observations.

Each of the 18 admitted `Run` implementations is still supplied through the
typed `LeafOracle`; neither the generated transition nor the refinement theorem
defines its cryptographic or native computation. The independently handwritten
`Specification/PrecompileFrameStageBReference.lean` does not import the
generated kernel and uses a reference-only decision/render interpreter instead
of duplicating the generated transition. `Refinement/PrecompileFrameStageB.lean` proves
`source_execute_precompile_refines_reference` for every generated `Input` and
every `LeafOracle` by explicit structural conversion. The Stage B manifest
hashes the accepted Stage A artifact triple, ten relevant production sources,
24 exact member identities, all 18 oracle identities, the independent
reference, the theorem source, the Stage B IR, and the generated Lean kernel.
The 24-member closure directly pins the standard `EthereumGasPolicy`
implementations for child creation, pricing, execution-gas clearing, halt
restoration, and refund, together with the pricing and state-transition
adapter/pure kernels. Child creation validates all five fields, including the
default-zero `StateGasSpillRefunded` field.

The typed input boundary admits the post-pop inline helper state: the six
`STATICCALL` operands were popped successfully, one result slot is available,
`inputMemoryValid` records `TryLoad` and, when it is true, `callData` is the
exact returned value. `outputCopyValid` records the result of the clipped
nonempty `TrySave`. `baseCost` and `dataCost` are the exact selected precompile
pricing results. Address, leaf selection, and the RIPEMD-160 exclusion flag are
resolver facts. Byte sequences use `UInt8`;
fixed-width gas and counter arithmetic is represented by `Nat`/`Int` only under
the no-overflow assumption recorded in the IR, except for the modeled pricing
overflow guard.

This theorem is conditional on the oracle and adapter assumptions recorded in
the IR. It does not prove cryptographic/native correctness, missing-dependency
process behavior, `WorldJournal` composition, surrounding CALL access or
EIP-150 reservation, the outer `RunByteCode` exceptional-halt handling or final
frame residue, transaction reachability, block processing, CLR/JIT, pooling,
unsafe memory, or whole-EVM correctness.
