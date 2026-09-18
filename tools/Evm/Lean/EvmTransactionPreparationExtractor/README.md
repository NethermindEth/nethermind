# EVM transaction-preparation extractor

This package admits the standard-mainnet Amsterdam ordinary transaction route
from the successful post-nonce `EvmHandoff` through the last operation before
`VirtualMachine.ExecuteTransaction`. It is an independent package under
`tools/Evm/Lean`; it does not modify production C#, `Evm.slnx`, root Lake
imports, or global manifests.

The C# executable is a fail-closed Roslyn extractor. It reads the reviewed
source/dependency closure, checks every source byte hash, builds an explicit
hash-pinned compiler/reference closure, and admits the relevant methods using
semantic symbols, `IOperation`, and control-flow graphs. Source text is retained
as evidence, but source spelling or caller-selected predicates are not the
grammar. Artifacts are written only after all closure, binding, CFG, IR, and
round-trip checks pass.

The boundary records ownership of `TxExecutionContext` and `StackAccessTracker`,
authorization validation and EIP-7702/EIP-8037 effects (including partial OOG),
recipient/access/code/delegation preparation, the post-intrinsic state
reservoir, dead-recipient state charge and whole preparation rollback, and
top-level CREATE classification/state charge before `PayValue`. It preserves
both snapshots, all five `EthereumGasPolicy` fields, physical/logical and
original/current world facts, account/storage warmth, refund/code-insert
counters, trace/access observations, environment/code/input identities, and
status/error. `VmState.RentTopLevel` is the final lowered operation; VM body,
frame execution, and tail settlement are excluded.

The package's enriched `EvmHandoff.access` is the entry projection for the
fresh tracker (the ordinary post-nonce sibling handoff has no tracker field).
The final modeled access observation is preserved separately in `Result.access`
and `VmInput.access` at the `RentTopLevel` boundary; it is not required to
remain fresh or equal to the handoff access.

`Generated/` is theorem-free typed Lean and contains no proof placeholders.
`Reference/` is separately structured handwritten semantics. `Refinement/`
states the universal relation only under explicit fixed-width, complete input
coherence, and typed source-entry obligations. Input coherence binds the
account, authorization, recipient, code, and value-transfer projections before
execution; it is an input predicate, not result agreement. The source-entry obligation is a conditional
production boundary: it excludes causeless pre-warmed access and nonzero
initial refund states while binding the entry/pre-execution world, code,
snapshot, and gas facts. A captured preparation snapshot is opaque outside the
exact source `TakeSnapshot` identity (with `Snapshot.Empty` bound to its
`EmptyPosition == -1` sentinel); the exact EIP-7702 authorization-list guard,
the nested EIP-8037 guard, and the `Transaction.Type == TxType.SetCode`
definition of `HasAuthorizationList` are admitted. CREATE recipient derivation
is also bound to the exact `TransactionExtensions.GetRecipient` body, while journal correctness
is not claimed. Successful static admission also excludes SetCode CREATE,
even when the modeled authorization list is empty. The fresh local tracker is
required to flow through both outer `ExecuteEvmCall` calls and their
`in accessedItems` parameter to `VmState.RentTopLevel`. Its
sibling theorem is deliberately identity-only:
it instantiates the imported ordinary-dispatch and authorization-fold theorems
at concrete projected inputs and checks exact theorem/artifact identities, but
does not claim that either sibling result equals this package's preparation
transition. That cross-package semantic adapter remains an explicit future
obligation. A byte hash is recorded as provenance, never treated as a semantic
proof.

The theorem remains model-to-model: its representation premise does not prove
nonnegative state reservoirs or no signed-counter wrap during execution.
Those admitted model states can differ from production `StateGasChargeKernel`;
the corresponding production reachability/arithmetic proof remains excluded.

`Refinement/Admission.lean` discharges the fixed operation, branch, binding,
CFG, and metadata gates; `Refinement/Maps.lean` contains the field and atomic
transition maps. The final refinement composes authorization, environment,
and call preparation. Its gate decomposition is proved for arbitrary gate
values and phase functions before instantiation with the admitted source
functions, avoiding expensive reduction of closed metadata during proof
checking. This adds no result-agreement assumption to the public theorem.

## Scope and validation

See [SCOPE.md](SCOPE.md), [SOURCE_AUDIT.md](SOURCE_AUDIT.md), and
[TEST_PLAN.md](TEST_PLAN.md). `Verify.ps1` is the package-local gate for the
serialized lane. It performs C# build/tests, fresh deterministic generation,
checked-in artifact comparison, Lake/Lean targets, and placeholder scanning.
`Verify-Axioms.ps1` preserves the 128 named Reference/Refinement export checks
and invokes `Verify-ClosureAxioms.ps1` over every theorem declaration originating
in the 22 imported repository modules: 3,273 public and 292 private declarations,
including compiler-generated descendants. `IMPORTED_THEOREMS.txt` freezes each
module's public/private counts and the SHA-256 of its ordinal-sorted complete
theorem-name roster (UTF-8, one name per LF-terminated line). Selection uses
module ownership, not theorem namespaces, so instances and declarations in
unrelated namespaces are included. Only `propext`, `Classical.choice`, and
`Quot.sound` are allowed. Fifteen closure controls exercise inventory drift,
missing/duplicate/malformed results, module additions/removals, unlisted public,
private and nested declarations, and explicit/native-decision axioms. The token
scan is supplemental.

Four unused journal/frame/account-pricing imports were removed from `Maps.lean`;
all theorem statements and proof bodies remain unchanged. The broader sibling
metadata dependencies remain pinned as provenance, and deterministic generation
retains the accepted IR, source-manifest and generated-Lean bytes. This import
and audit hardening is pending independent rereview; it does not expand the
accepted conditional model-to-model boundary.
It must not be run until the parent task releases that lane.
