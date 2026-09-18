# Source audit

The standard-mainnet Amsterdam and `EthereumGasPolicy` labels are declared
target context only. The admitted `VirtualMachine<TGasPolicy>` source is open
over its gas-policy parameter; fork/policy wiring is outside this package.

The admitted source snapshot is the current repository production closure
listed in Admission/ProductionClosure.txt. Every source byte is checked
against its lowercase SHA-256 before Roslyn parses it. Every selected member
is resolved by namespace, nested owner path including generic arity, member
kind/name, generic arity, canonical parameter tokens, Roslyn syntax kind, and
the complete token-stream SHA-256.

The extractor derives a preorder topology of recognized control and
call-bearing nodes from the method AST instead of copying a handwritten member
body into Lean. It records if/while/for/try,
catch, return/continue/goto/label/throw/using and call-bearing local-statement
nodes (and finally nodes when present), their parent and source arm, canonical
condition tokens, complete node-token hashes, and calls. The five generated
shell stages and every named branch carry
concrete bindings to those nodes, including each node hash. The emitted plan
pattern matches every admitted stage/member/node/kind/arm/hash identity. `stepOnce`
consumes and validates the emitted plan before calling the parametric leaf
algebra; all operation bodies remain explicit adapter leaves, so topology
admission is not itself a semantic
proof of production effects. Until those adapters and effects are related to
the C# body, this remains an audit scaffold rather than a production
operational refinement.

The generated source-control witness also retains the required operation
markers for each admitted anchor. The readiness gate checks those markers,
the member set, every branch binding, and every plan node hash before allowing
the algebraic step; these checks establish source-evidence consistency only.
The admission and source manifest also pin all four accepted refinement files
and their theorem identities; those pins keep dependency provenance explicit,
but do not import their leaf semantics into this shell.
The cancellation branch is bound to exactly two source polling statement nodes
in `RunDispatchLoop`, preserving the entry and post-batch topology separately;
an enclosing loop or condition is not accepted as a polling-site identity.

The production observations that determine this boundary are:

- ExecuteTransaction clears return data only for non-continuation frames;
- precompile frames use ExecutePrecompile, while bytecode frames use
  ExecuteCall and PrepareNextCallFrame;
- a returned nested frame is merged or reverted, with create code-deposit
  branches between return and parent resume;
- top-level return takes the top-level substate path;
- cancellation and callback escapes unwind through FrameCleanupScope.

These observations justify the one-step boundary but do not establish
reachability or a complete transaction/block proof.
# Stage F operational audit (separate from the accepted Stage E shell)

Stage F remains unaccepted pending serialized artifact generation, direct Lean
gates, and concrete production adapter obligations.  The conditional
finite-fuel equality described here is the candidate theorem boundary, not a
current production-refinement claim.

The existing Stage E source/member hashes and four Roslyn control anchors are
preserved. Stage F consumes that exact boundary and emits a different kernel,
IR, and source manifest. It does not rewrite or relabel the Stage E artifact.

The operational loop is source-shaped at the `ExecuteTransaction` boundary:
fresh frames clear return data before preparation, continuation frames preserve
the prior child result, the current state selects bytecode versus a full-frame
precompile, and each dispatch outcome is settled before the next finite-fuel
iteration. `RunDispatchLoop` has four explicitly named table modes; cancelable
modes carry the entry poll and a poll after every completed nonterminal
1024-opcode epoch. Terminal batches may end early; the non-cancelable tail-call
  chain is one terminal batch with its exact instruction count. Poll counts are
  cumulative across 1024, 2048, and later nonterminal epochs. Malformed
batch/route vectors fail closed. Child suspension stores a parent and resumes
the head of the stack.

 The generated transition lowers the typed control plan and branch bindings to
an `OperationalControlInstruction` stream. Each branch carries typed
predicate/effect/settlement identities and the source-derived stage set where
it is valid, so shared classify/settle branches are emitted in both stages. Its generic
  `executeControlInstructions` interpreter checks source stage order, branch
  attachment, and the exact typed predicate/effect/settlement/action shape
  selected for each source branch before the frame transition runs. The C# IR,
  source manifest, and Lean plan carry the aligned action set, which is checked
  against the exact Roslyn/CFG topology identity. It then matches that
  instruction against the live machine/step/result observation at each stage;
  the selected action is therefore determined by the emitted semantic IR,
including the dedicated parent-resume instruction used after child settlement.
Every
stage record is also looked up in the emitted topology and checked against its
canonical structural shape. A plan, topology, branch, or semantic-effect
mutation consequently selects an incomplete route rather than being retained
as inert metadata.

The settlement carrier keeps separate operations for regular return, post-child
CREATE code deposit, delegated CALL/CREATE collision, revert, and exception. A
deposit result is accepted only when its typed deposited/invalid-code/out-of-gas
outcome agrees with the parent-continuation success/failure marker. Its fields
include execution gas, state-gas reservoir/used/spill/refund, refund merge and
rollback, snapshots, access/log/destroy/RIPEMD state, return-data and parent
output-copy metadata, trace/substate/status, and active-frame disposal. Missing
adapter output is an explicit `IncompleteReason`, including cancellation and
escape cleanup; it is never a successful default.

The independent reference repeats this control order without importing the
generated kernel. The refinement derives one-step generated/reference equality
from separate adapter equalities and intermediate admitted-state closure, then
  uses induction over explicit fuel with per-adapter equality and admitted-state
  closure. Exact generated route-table, reachability, and stack-shape
  obligations are exposed separately; the finite-fuel equality does not
  silently consume them.
`ProductionAdapterObligations` remains an uninstantiated, per-adapter semantic
boundary. The independent mutation graph executes total fixture callbacks
through `runFuel`; refund, return-data, world, cleanup, route-evidence, and
malformed control-route mutations are observed in actual operational outputs,
preventing route-only or metadata-only tests from vacuously passing.

The operational carrier requires adapter-supplied bytecode dispatch-route
evidence from the admitted dispatch table, one aligned route/PC witness for
every reported completed opcode, with cardinality and byte-lookup checks, and
an address
route for every full-precompile result. This route stream is an evidence
premise rather than a proof of the C# handler's exact execution order or
internal control. It also records each batch's starting PC/opcode count and
final successor/count; per-op successor/control transitions, including
variable-width PUSH handling, remain an explicit adapter obligation. The
driver rejects missing/duplicated route evidence, a successor/count drift, or a
  resume whose captured parent head/call-depth stack shape is inconsistent. PC,
  gas, operand stack, memory, world, and output continuation equality remain
  separate adapter obligations. The
entry-adapter module implements only the source-visible fresh clear;
continuation preparation remains `none` until the source stack/child-result
effects are individually simulated, and all other stateful production leaves
also remain fail-closed. Direct-inline STATICCALL behavior is deliberately not
a frame-driver field: it belongs to the `ExecuteCall`/CALL/CREATE opcode leaves.
Stage F does not claim CALL/CREATE source closure; accepted Stage A routing is
only the typed leaf dependency.

Accepted Stage A routing/opcode theorem identities, precompile Stage B/C,
WorldJournal/FrameJournal, state-gas/pricing, and Stage D identities are
validated by exact manifest/proof hashes and Lean `#check` witnesses. Roslyn
IOperation/CFG/type/symbol checks run over the pinned full compiler reference
inventory (including exact bytes and MVIDs), and call-bearing nodes require
exact non-error, non-candidate `IMethodSymbol` targets before artifacts are
emitted. The loop identity additionally extracts `CancellationCheckMask`, the
four opcode-table fields, and the `while (true)`/two cancellation-call/bitmask
shape from the admitted dispatch source before emitting the typed loop plan.
These checks establish closure identity, not the missing production
adapters. The theorem starts from a preconstructed VM/frame state and excludes
`TransactionProcessor.CompleteWithoutFrame` and
`TransactionProcessor.FailContractCreate` bypass paths. Fuel adequacy from the
  gas/PC/frame-stack measure for continue and suspend/push transitions is visibly
  blocked, and no whole-EVM or
transaction/block claim is made.
