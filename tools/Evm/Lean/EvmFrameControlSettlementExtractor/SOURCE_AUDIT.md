# Stage D production source audit

Every closure file is byte-hash pinned in `Admission/ProductionClosure.txt` and
every listed C# member is Roslyn-selected. This audit records the production
facts used to review the handwritten Stage D transcription; it is not a claim
that the extractor translates statement bodies.

## VM driver boundary

`VirtualMachine.cs:202-372` is the actual loop boundary. It clears
`ReturnDataBuffer` only when `!_currentState.IsContinuation` and before fresh
frame dispatch, executes
`ExecutePrecompile` whenever the current state is precompile, otherwise starts
fresh action tracing/transfers and calls `ExecuteCall`. A non-return result
prepares a child and continues. A top return invokes
`PrepareTopLevelSubstate`, clears `_currentState`, and returns to its caller;
it does not run transaction-processor deployment/refund/finalization here.

Nested completion pops the parent, marks it continuation, refunds/merges
success or restores gas/snapshot on revert, and derives CREATE handling from
the completed current child's `previousState.ExecutionType`. It invokes
`HandleCreate` only after successful child initcode completion.
`TryChargeAndDepositCode` is thus a nested settlement decision after initcode
success/refund, not an opcode result tag. The `FrameCleanupScope` at
`VirtualMachine.cs:396-407` clears residual
child frames and the state stack during exceptional unwind; normal completed
exits clear VM fields first and make the scope a no-op. It intentionally does
not dispose the top state or restore external world state.

## Dispatch, precompiles, and cancellation

`VirtualMachine.Dispatch.cs:141-204` returns normally before cancellation if
the program counter is already past code. For a cancelable table it polls once
before the first opcode. Its later poll happens only after a normal result, a
positive completed 1024-opcode batch, and a successor program counter; terminal/error
results at the mask boundary break first. Stage D represents exactly those two
bytecode driver observations and admits no full-precompile driver cancellation
poll. `CancellationTxTracer` can also throw from tracing callbacks outside that
dispatcher (including a precompile action trace); Stage D represents this as an
escaped invocation/unwind rather than inventing a poll boundary.

The direct STATICCALL fast path is in
`Instructions/EvmInstructions.Call.cs:269-275` and its helper. It occurs while
executing a bytecode parent, before a child `VmState` is rented. It is modeled
only as an optional bytecode opcode outcome. Conversely, a current
`IsPrecompile` frame always uses the full `ExecutePrecompile` route, with
top/nested classification derived from `IsTopLevel`. A successful full route
completes that current non-CREATE frame with `PrecompileSuccess == true`; it cannot become a bytecode
continuation or child-frame suspension.

## Settlement and observations

`CallResult`, `VmState`, `VmStateStack`, `EvmPooledMemory`, gas policy,
state-gas, code-deposit, world-journal, and tracer sources identify the adapter
surface. Stage D exposes state, returndata/output and parent-copy metadata,
the RIPEMD restore latch, journal/world, all execution/state-gas baselines and
counters including `VmState.Refund`, trace, and disposal observations, but each
concrete effect is a future production-simulation obligation. In particular,
EIP-8037/refund/journal behavior is not proved by the shared canonical leaves
or by source identity.

The closure additionally names the critical driver helpers rather than relying
only on the enclosing file hash: cancellation throw and both dispatch exit
forms, full and inline precompile execution, child/pop/revert/CREATE helpers,
top-level state rent, and pooled-memory disposal.

## Caller context and exclusions

Transaction processor members are retained in the source closure to document
the outer owner boundary and cancellation/rollback context. They are not
composed into the theorem: top-level deployment, `Refund`,
`FinalizeTransaction`, transaction-scope disposal, and caller world rollback
are outside Stage D. Whole-loop induction/fuel reachability is also open.

## Imported formal packages

Stage A routing, Stage B inline precompile, Stage C full precompile, state-gas,
and pricing package theorems are hash-pinned and named. `SourceAdapterBindings`
is an explicit assumption interface: it carries their identity-checked witnesses
and assumes that canonical bytecode/full-frame leaves equal caller-supplied
oracle values on admitted states. It does not discharge semantic adapter
composition. `ProductionLeafSimulation` separately states the dispatch-derived
settlement and cleanup obligations. Artifact identity and generated `#check`
commands do not establish production semantic composition by themselves.

## Stage C full-frame bridge boundary

`Refinement/StageCFullPrecompileBridge.lean` is non-generated and does not extend
the source closure. Its `StageCAdmission` constructs a use of the existing
Stage C theorem from `Entry.Admitted`, front/oracle agreement, parent
preservation and top-adapter shape. Its separate Stage D evidence theorem uses
the admitted iteration, source bindings, driver completion-domain witness and
output bounds. Neither theorem relates the two components' execution results.

`OpenFullPrecompileCompositionObligation` is deliberately unproved. The live
source explains why a tag-only bridge is insufficient: `RunPrecompile` touches
the account and conditionally sets the RIPEMD latch before pricing, installs
priced gas before the oracle, and `ExecutePrecompile` can clear nested gas.
`ExecuteTransaction` then settles using the same mutable `_currentState`.
Stage D's failure tags retain only their kind/error, while its settlement
functions remain opaque. Stage C retains that state in `RawResult`, but its
top-level `Settlement.complete` later retains only `FrameResult`. A concrete
adapter must preserve this state and supply the missing complete terminal
machine/trace/disposal observations. No production bug was identified in these
source paths; the defect was the formal composition claim.

The observation now preserves the entire Stage D observation and independently
records substate error. Journal representation and trace ownership are future
adapter work, along with source/Roslyn, crypto/native, CLR/JIT/AOT, unsafe/pool,
fixed-width and tracer correspondence. No hardcoded no-parent-resumption flag
is used: the one-iteration driver selects one frame, but opaque leaf internals
are not proved. Missing native support produces `none` in Stage C and an
incomplete verification-harness result, reflecting the production process-exit
exclusion rather than a fabricated successful VM result.
