# EvmFrameControlSettlementExtractor (Stage D)

Stage D is an admitted-step control-agreement check for one
`VirtualMachine.ExecuteTransaction` loop iteration for standard-mainnet
Amsterdam. Its public theorem is
`hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement`;
there is no public whole-run/fuel theorem and no claim that production C# leaf
implementations have been simulated.

`Admission/ProductionClosure.txt` pins exact production/dependency bytes and
Roslyn member identities. This establishes source/member identity and drift
detection. It does not extract C# statement-body semantics: the generated
driver and decision-oriented reference are separately handwritten operational
transcriptions, reviewed against that pinned source surface.

The generated driver and independently planned reference use one shared
`CanonicalLeaves` record. Their theorem checks fresh versus continuation setup,
bytecode versus current-frame full-precompile selection, each VM-frame
settlement route, and terminal cleanup. `SourceAdapterBindings` is an explicit
assumption interface: it carries identity-checked Stage A--C/gas/pricing theorem
witnesses and assumes that canonical bytecode/full-frame leaves equal
caller-supplied oracle values on admitted states. It does not discharge semantic
adapter composition, which remains open. `ProductionLeafSimulation` is
separately stated, explicitly open,
and scopes settlement/cleanup legality to a derived `AdmittedStep` (input,
invocation, route), never an arbitrary pair. Its observation includes
status/exit, return/output data and parent-copy metadata, the RIPEMD touch
restore latch, world and journal fields, execution/state-gas counters and
baselines, both refund counters and `VmState.Refund`, traces, the bytecode-only
direct-inline STATICCALL event, and disposal ownership.
`Specification/AdmissionWitnesses.lean` constructively witnesses
bytecode terminal, bytecode continuation/suspend/direct-inline-event, and
  full-frame-precompile shapes, then instantiates the complete control and
  production-obligation interfaces with explicitly nonproduction synthetic
  leaves so those admissions are not vacuous.

`Refinement/StageCFullPrecompileBridge.lean` records the typed
`OpenFullPrecompileCompositionObligation`; it proves no Stage C-to-Stage D
refinement. The checked theorems separately establish Stage C refinement and
Stage D control agreement, admitted output bounds, source-oracle binding and
the full-precompile completion domain. The observation retains every Stage D
field plus substate error separately from status. No arbitrary settlement
projector or assumed whole-result equality is accepted by a theorem.

The missing adapter is substantive: Stage D failure tags omit Stage C's mutated
machine/result, while Stage C's top-level settlement omits the full terminal
machine, transaction trace and disposal ownership. The open obligation states
preparation, dispatch, route and represented-settlement correspondence. Even
discharging it would require additional source observations before claiming
full observational equality. Concrete journal projection and trace ownership,
source/Roslyn interpretation, crypto/native, CLR/JIT/AOT, unsafe/pool and tracer
semantics remain open. The driver invokes one selected frame in this iteration;
it does not prove that an opaque leaf never executes parent opcodes internally.

`Refinement/StageCFullPrecompileBridgeVectors.lean` constructs admitted Stage C
inputs and run witnesses for twelve top/nested success, returned/managed
failure, pricing overflow/out-of-gas and native-unavailable cases, invokes the
Stage C theorem, and checks separate status/substate-error observations. The
18-address registry is an address inventory, not native-body proof. Native and
process unavailability produce explicit incomplete harness results; production
missing-native handling exits the process. The verification scripts compile
the modules directly with warnings as errors and reject four semantic Lean
mutations. These checks do not close the composition obligation.

The shared frame-machine carrier has a `running` phase for lower-level
packages, but Stage D admits only the two production loop-boundary phases:
fresh and continuation.

Important boundaries:

* A direct precompile is an outcome inside a bytecode opcode before a child
  frame is rented; it is never a frame-dispatch subject.
* A full precompile is selected from the current frame, completes that
  non-CREATE frame with nullable `PrecompileSuccess = true` (or takes a named
  failure leaf), and its top/nested split reads the current
  `VmState.IsTopLevel` flag while admission establishes the parent-stack
  topology invariant.
* Nested CREATE code deposit follows successful initcode return and refund; the
  VM derives that branch from the current child frame's execution type, and it
  is a settlement decision, not an opcode invocation tag.
* The `.cancelled` outcome denotes only cancelable `RunDispatchLoop` polling:
  before its first opcode and after a normal completed 1024-opcode batch with a
  successor. The completed batch count is positive. Full precompile execution
  has no blanket driver poll. A `CancellationTxTracer` callback can instead
  throw outside that loop (including around action tracing); that abrupt path is
  represented as `.escapedInvocation`, not a driver poll.
* `OperationCanceledException` unwinds the VM through `FrameCleanupScope`.
  The scope drains child frames only on that exceptional path; a normal
  completed exit is a scope no-op. Top `VmState`, pooled memory, and world
  rollback are explicit external caller obligations.

Whole-loop induction and reachability, outer transaction deployment/refund/
finalization, caller rollback, opcode/native/precompile-body correctness, and
transaction/block processing are explicitly open or excluded.

Run the standalone gate from this directory after the pinned toolchain is
available:

```powershell
./Verify.ps1 -RepoRoot D:/base/projects/nmc/formal
```
