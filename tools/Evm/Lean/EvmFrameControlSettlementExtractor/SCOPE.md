# Stage D scope and acceptance boundary

## Closed root

The admission fixes `VirtualMachine<EthereumGasPolicy>.ExecuteTransaction<TTracingInst>`
at `Nethermind.Specs.Forks.Amsterdam`. The closure includes its dispatch and
frame-state helpers, CREATE/CALL and precompile routes, gas/state-gas and
journal/tracing interfaces, transaction-processor callers for ownership
context, DI wiring, and fork lineage through Amsterdam.

The byte hashes, normalized declaration signatures, and selected member names
prove only exact source/member identity. The Stage D operational IR is a
reviewed handwritten transcription; it is not a C# body extractor.

## Checked boundary

The central theorem relates one generated `driveIteration` to one independently
planned reference `evaluateIteration` over the same `CanonicalLeaves` record.
It is a control-agreement check, not a theorem that production C# implements
those leaves. It covers:

* fresh-only return-data clearing and continuation preservation;
* bytecode versus full current-frame precompile dispatch;
* direct-inline STATICCALL as a bytecode opcode event, not a frame route;
* returned, suspended, thrown, escaped, and exact bytecode-cancellation
  outcomes;
* regular, nested CREATE/deposit (classified from the current frame's
  execution type), revert, exception, and full-precompile
  top/nested settlement leaves;
* terminal cleanup ownership; and
* observations of status/exit, returndata/output and parent-copy metadata, the
  RIPEMD restore latch, world/journal, all execution and state-gas/refund
  counters and baselines,
  logs/traces, direct-inline outcome, and disposal.

`topLevel` is exactly the current frame's `IsTopLevel` flag. The admitted-state
predicate separately supplies the exact `IsTopLevel ↔ empty-parent-stack` shape
invariant rather than replacing the production classification rule with stack
shape.
It also excludes the shared carrier's lower-level `running` phase: a production
`ExecuteTransaction` loop boundary is either fresh or continuation.

## Assumptions and admissions

`SourceAdapterBindings` is an explicit assumption interface: it carries
identity-checked imported Stage A, B, C, state-gas, and pricing theorem
witnesses and assumes that canonical bytecode/full-frame leaves equal
caller-supplied oracle values on admitted states. It does not discharge semantic
adapter composition; a dependency hash or `#check` is not a semantic proof.
`ProductionLeafSimulation` separately lists future per-leaf implementation
obligations. Its settlement and cleanup leaves are legal only for `AdmittedStep`
values whose invocation and route are derived from the production
dispatch/classifier, not arbitrary pairs.
Fixed-width bounds apply to the input, fresh intermediate, prepared state,
bytecode/full dispatch output, bytecode/full settlement output, and final
iteration output.

The full-precompile adapter has its own admitted outcome domain: success halts
the current non-CREATE frame with nullable `PrecompileSuccess = true`, while its named out-of-gas, returned-failure, and
managed-exception cases take their own leaves. It cannot use bytecode
continuation, suspension, or driver-cancellation outcomes.

`Refinement/StageCFullPrecompileBridge.lean` states an OPEN composition
obligation for a current already-created precompile, restricted to the full
entry domain. It separately proves the Stage C component refinement and Stage
D component control/domain/bounds facts, and retains complete Stage D
observations plus substate error. No theorem proves equality between Stage C
and Stage D. The open relation covers only fields present in Stage C's
settlement carrier; a complete top-level machine, trace/disposal ownership and
concrete journal correspondence still require a source adapter. Failure-state
transport through Stage D's tag-only invocations is also unresolved.

Twelve top/nested component vectors execute actual admitted Stage C inputs and
invoke its theorem. The 18-address list remains a registry inventory. Native
and process unavailability fail closed in the verification harness, and four
Lean-file mutation gates guard these checks. Direct-inline STATICCALL and
outer exception entries are outside this narrowed composition interface. The
driver's one-frame selection does not prove absence of parent opcode execution
inside opaque leaves; no constant no-resumption observation is asserted.

Cancellation admission mirrors `RunDispatchLoop`: only a cancelable bytecode
path may yield the `.cancelled` driver outcome, after the initial code-boundary
check before the first opcode or after a normal positive completed 1024-opcode
batch with a successor. Full-frame precompile execution has no driver
cancellation poll. `CancellationTxTracer` callbacks may still throw outside the
dispatcher, including around action tracing; that abrupt path is an
`.escapedInvocation`, not a claimed poll boundary. Cancellation is an abrupt VM
unwind; caller rollback ownership is not discharged here.
`FrameCleanupScope` is a no-op after a normal completed VM exit and is modeled
only for abrupt cleanup paths.

## Explicit exclusions and open work

Stage D does not prove a whole loop, fuel adequacy, or reachability of each
successive admission. It excludes outer transaction processing (`DeployContract`,
`Refund`, `FinalizeTransaction`), caller world rollback/top-state disposal,
opcode bodies, cryptographic/native leaves, concrete world/trie/database
behavior, transaction validation/receipts/block processing, RPC/networking,
concurrency, CLR/JIT/AOT/unsafe behavior, and arbitrary fork or gas-policy
configurations.
