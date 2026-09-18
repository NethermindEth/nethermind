# EVM frame-driver Stage E and Stage F package

Stage E remains the accepted source-attached one-step audit scaffold described
below. It is intentionally unchanged. Stage F is a separate operational
artifact family (`--stage operational`) and does not silently widen the Stage E
claim.

## Stage F operational slice

Stage F is currently an unaccepted candidate.  The generated operational
artifacts and direct Lean gates must still be produced in the serialized
validation lane, and the production adapter obligations below remain open.

Stage F emits a theorem-free typed IR and generated Lean for the explicit
finite-fuel `ExecuteTransaction` frame loop. Its source-derived control order
is prepare, dispatch, classify, settle, cleanup. The generated loop consumes
 the four `RunDispatchLoop` tables (NoTrace, NoTraceCancelable, Traced, and
 TracedCancelable), validates exact 1024-op nonterminal cancelable epochs
 (including cumulative 2048+ epoch poll counts, and
 the exact terminal count of the non-cancelable tail-call chain) with successor
 PC accounting, dispatches full-frame precompiles, pushes child frames and
 resumes parents with adapter-checked LIFO stack shape only (not parent-value
 identity), and routes success, revert, exception,
CREATE-deposit/collision, cancellation, escape, and cleanup outcomes.

The typed plan is lowered to an executable `OperationalControlInstruction`
stream. Each instruction carries typed source predicate, ordered effect, and
settlement identities, a typed lowered action, a source-derived stage set, and
an admitted branch. The C# operational IR and source manifest serialize those
typed identities and the Lean branch binding consumes them directly; the
per-stage plan action set is derived from those bindings and checked against
exact Roslyn/CFG topology identities. The generated interpreter matches those
predicates against `Machine`, `MachineStep`, `FrameResult`, create-deposit, and
cleanup observations; its effect list is part of the executable action shape,
not an inert label. Source stage order, branch bindings, and effect/stage
 compatibility are checked before `evaluateStep`/`runFuel` can enter a
 transition; parent-resume is a separate typed settle instruction over the
 captured stack shape (call-depth/LIFO shape only; PC, gas, operand stack,
 memory, world, and output continuation equality remains an adapter
 obligation). Plan, topology, branch, or semantic-effect drift reaches an
explicit incomplete route.

The carrier exposes state-gas reservoir/used/spill/refund operations, world and
frame journal effects, access/log/destroy/RIPEMD state, tracing/substate/status,
return-data and bounded parent-output copying, and `DisposeActiveFrames` as
individual adapter boundaries. CREATE success is routed through a distinct
typed code-deposit leaf (deposited, invalid-code, or out-of-gas) after the
child succeeds; the leaf must return the matching parent-continuation success
or failure marker. CREATE collision is delegated to the accepted Stage A
CALL/CREATE routing leaf. Every adapter is `Option`-valued; `none` is an
explicit incomplete result. No byte pin, oracle name, caller boolean, or
accepted theorem identity is used as a semantic proof.

The frame carrier intentionally has no direct-inline field: STATICCALL and
other direct invocation behavior is owned by the `ExecuteCall`/CALL/CREATE
opcode leaves. Stage F does not admit the CALL/CREATE source closure; the
accepted Stage A routing package is only a typed leaf dependency and does not
prove those opcode bodies.

Each bytecode result also carries adapter-supplied dispatch-route evidence for
the four dispatch tables. Its route stream has one aligned route and PC witness
per reported completed opcode, with cardinality and byte-lookup checks, so
dropping or duplicating dispatch evidence fails closed; the list order and
per-op control are supplied by the production adapter, not proved by this
carrier. Batch records expose only their
starting PC/opcode count and final successor/count, so per-op successor/control
transitions (including variable-width PUSH handling) remain an explicit
adapter obligation. Each full-precompile result carries its admitted address
 route. A child resume validates only the head/call-depth shape of the
 captured parent stack.
The small entry adapter in
`Specification/OperationalAdapters.lean` closes only fresh return-data
clearing; continuation preparation remains fail-closed because the source
stack/child-result effects are not yet simulated.

`Specification/OperationalReference.lean` is independently organized and does
not import the generated kernel. `Refinement/OperationalFrameDriver.lean`
derives `adapters_step_agree` from separate per-leaf adapter equalities and
explicit preparation/failure/output admitted-state closure, then proves
 `generated_runFuel_reference_runFuel` by finite-fuel induction. Exact route
 tables, reachability, and the stack-shape relation are carried as separate
 domain obligations and are not silently consumed by that equality theorem.
`ProductionAdapterObligations` records the separate per-leaf production
obligations; no instance is claimed here.
`Specification/OperationalVectors.lean` executes total fixture adapters through
`settleHalt`/`runFuel`; settlement, cleanup, route-evidence, and malformed
control-route mutations are observed in the resulting operational output.
Fuel adequacy for both continue and suspend/push transitions from the
gas/PC/frame-stack measure is an explicit blocker, so Stage F is not a
whole-EVM or transaction-completeness claim. The operational theorem starts
from a preconstructed VM/frame state and excludes
`TransactionProcessor.CompleteWithoutFrame` and
`TransactionProcessor.FailContractCreate` bypass paths, intrinsic transaction
validation, caller rollback, and outer transaction finalization.

The Stage F extractor first runs the accepted Stage E source/member admission,
Roslyn IOperation/CFG/type/symbol checks, exact compiler MVID closure checks,
and all accepted dependency hash/theorem checks. It also extracts the
`CancellationCheckMask`, exact four dispatch-table fields, and cancelable
`while (true)` shape from the admitted dispatch source; it then writes three
deterministic artifacts named `EvmFrameDriverOperationalKernel.*`.

## Stage E accepted shell (unchanged)

The following describes only the accepted Stage E source-attached shell. It
is not the Stage F finite-fuel driver above. Stage E provides a theorem-free,
source-attached one-step carrier at the generic `ExecuteTransaction` boundary.
Roslyn admits exact source members in VirtualMachine.cs,
VirtualMachine.Dispatch.cs, and VmState.cs; it also extracts a preorder
topology of recognized control and call-bearing nodes from ExecuteTransaction,
RunByteCode, RunDispatchLoop, and FrameCleanupScope.Dispose. Topology nodes
carry their parent, source arm, condition, token hash, and calls; both
RunDispatchLoop cancellation polling sites are retained as exactly two
call-bearing statement nodes; the enclosing loop/if nodes are not treated as
poll sites.

Amsterdam and `EthereumGasPolicy` are declared target-context labels for this
audit. The admitted C# member remains open over `TGasPolicy`; this package does
not prove the fork/policy wiring that selects that instantiation.

The accepted Stage E generated Lean kernel describes, in order:

1. fresh/continuation preparation and fresh-only return-data clearing;
2. bytecode versus full-precompile dispatch;
3. one returned/suspended/continued/exceptional classification;
4. child-return, create code-deposit, top-level, or precompile failure
   settlement through named leaves; and
5. exceptional/cancellation cleanup.

The Stage E emitted plan attaches each prepare/dispatch/classify/settle/cleanup stage
to an extracted topology node. The emitted `executePlan` pattern is populated
from every admitted stage/member/node/kind/arm/hash identity; generator-side
source-control validation also checks required operation markers, and
`stepOnce` fails closed if a binding is absent. This is the mechanical
source-evidence-to-algebra connection. The branch effects themselves remain
parametric leaf adapters. Stage E is not a translation of the C# method body
or a complete production execution proof; its accepted boundary remains a
one-step audit scaffold.

Stage E `runOne` is intentionally a one-step boundary: fuel zero returns
FuelResult.exhausted without invoking a leaf, and any positive fuel executes
exactly one step. It is not the Stage F whole-run driver.

Stage E `Specification/Reference.lean` is independently organized around entry and
settlement plans. Refinement/FrameDriver.lean proves the generated one-step
control algebra agrees with that reference under fieldwise equality of the
leaf functions. This is an admitted-plan algebra theorem, not a proof that
production C# adapters satisfy those equalities. Accepted opcode routing,
frame-journal, full-precompile, and prior settlement packages are identity-pinned
generated/proof dependencies; their native bodies and concrete production
adapters remain outside Stage E. The source manifest pins all four referenced
refinement theorem identities, but those theorems are witnesses for their own
packages, not production leaf simulations for this shell.

The package does not modify parent solution/lake imports, production C#, the
global verification manifest, or the top-level README. The global complete-EVM
claim remains incomplete.
