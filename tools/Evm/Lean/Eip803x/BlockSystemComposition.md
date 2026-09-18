# Block/system-transaction composition reference

`BlockSystemComposition.lean` is a handwritten, executable composition layer
between the accepted `SystemTransactionReference` block-call adapter and the
accepted `BlockReference` / `BranchReference` relations. It is not an
extraction of C# and it is not a production-refinement theorem.

Pinned Nethermind revision:
`b2478235e71e6a7ec2a509aa0155e25d5fdfff80`.

## Boundary

The adapter accepts one block instance only after checking all of these facts:

- The beacon call is the exact `beaconRoot` call shape accepted by
  `SystemTransactionReference.runBlockCall`. Its processor entry world has the
  state token produced by the DAO hook, its committed world is the same world,
  and its block adapter contains no ordinary receipts, cumulative receipt gas,
  or request payloads. A missing target may skip the call. An applied call must
  expose a processor result and zero normal block-counter deltas.
- The block prefix through withdrawals actually completes under the composed
  `BlockSpec`. Its receipt IDs and cumulative receipt gas are copied into the
  initial request adapter. The full processor world supplied as an explicit
  premise must carry that prefix's state token.
- The request calls are exactly EIP-7002 withdrawal, EIP-7251 consolidation,
  EIP-8282 builder deposit, and EIP-8282 builder exit, in that order. Each call
  must be enabled, start from the previous call's full world and adapter state,
  complete successfully, preserve the ordinary receipt/cumulative-gas fields,
  and expose zero normal block-counter deltas. A tracer-reported request
  failure becomes `CompositionRun.invalidBlock`; unsupported or mismatched
  facts become `CompositionRun.unmodeled`.
- Deposit payloads, payload encoding, and request hashing are explicit adapter
  premises. Validation runs the four accepted call adapters, checks the final
  payload list against the supplied encoder, then checks the supplied hash
  function. The `BlockReference` request observation is installed only after
  those checks succeed.

The private admitted adapter implementation replaces only four abstract `BlockSpec` hooks:
`applyBeaconRoot`, `computeExecutionRequests`, `applyExecutionRequests`, and
`computeRequestsHash`. Every other block hook is preserved verbatim.

The raw block and branch record-update helpers are private module definitions;
no public function returns either composed `BlockSpec` or `BranchSpec`. The
sole public branch admission entry is `runValidatedSingletonBranch`. It
accepts exactly one block, runs `validateAdapter` for that block, checks that
the branch entry state equals the validated block entry state, and only then
invokes `BranchReference.runBranch`. Empty or multi-block lists fail closed;
constructing one adapter per block in a multi-block branch remains open. The
structural update leaves selection, world-scope, inclusion, prewarm,
CommitTree, and finalization hooks unchanged.

## Proved composition properties

The model proves:

- the composed block keeps the accepted eleven-step `ProcessOne` order;
- every accepted composed block has the exact `Phase.processOne` trace;
- beacon and request hooks install the system relation's checked state tokens;
- neither system hook changes `TransactionGas.BlockGas`;
- the processor's effective system options produce zero normal header,
  execution, and state counter deltas;
- the final request observation is visible in both block artifacts and the
  header only with its checked hash, with adjacent extracted/hashed control
  observations at the request stage;
- a later rejected block phase restores the complete entry `BlockWorld`;
- an admitted singleton reaches the synchronous branch relation only after
  adapter validation; the private structural update changes only the block
  field, so the resulting witness retains the supplied outer branch behavior;
  and
- positive-value CREATE cannot satisfy any pinned beacon/request call shape,
  because those shapes require both a concrete destination and zero value.

`BlockSystemCompositionVectors.lean` contains 14 named executable scenario
vectors and 10 mutation vectors. The successful witness threads one beacon call
and all four request calls through distinct processor worlds, retains the sole
ordinary receipt and its cumulative paid gas, installs the literal encoded
request value and hash, and completes a synchronous branch. Failure witnesses
cover beacon REVERT and exception rollback, request REVERT, exception, and an
abstract top-frame state-gas exhaustion, invalid tracer/fork/body facts,
unreachable positive-value CREATE, and a downstream rollback. Mutations break
beacon/DAO order, charge a system call as a user transaction, reverse request
calls, alter encoding or hashing, change the receipt/deposit projection,
contradict target code, disconnect the request world from the post-withdrawal
state, try to reuse the adapter for a second block, and enter the singleton
branch with a state different from the validated block. The encoding and hash
mutants are evaluated through the singleton branch wrapper and prove that
neither can reach an accepted branch.

The source contains 20 named model theorems, of which 12 are public admission
or observation theorems, and 14 named vector theorems. The
aggregate vector theorems prove that all 14 scenarios pass and all 10 mutations
are killed.

## Production anchors

All anchors refer to the pinned revision.

- `src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs`,
  `ProcessBlock`: beacon-root storage precedes the ordinary transaction fold;
  post-transaction state is committed; rewards and withdrawals follow receipt
  preparation; withdrawal state is committed before execution requests;
  requests precede trace finalization, storage/state roots, background receipt
  installation, and BAL finalization.
- `src/Nethermind/Nethermind.Blockchain/BeaconBlockRoot/BeaconBlockRootHandler.cs`,
  `StoreBeaconRoot`: eligibility and account existence gate the exact
  zero-value, 31,566,720-gas, address-access-list `SystemCall` from
  `Address.SystemUser`; the returned `TransactionResult` is ignored.
- `src/Nethermind/Nethermind.Consensus/ExecutionRequests/ExecutionRequestsProcessor.cs`,
  `ProcessExecutionRequests` and `ReadRequests`: deposit extraction precedes
  the four ordered system calls; missing code and failed `CallOutputTracer`
  status invalidate the block; empty output adds nothing; nonempty output is
  prefixed by its request-type byte.
- `src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs`,
  `Process`, `ExecuteCore`, and `UpdateHeaderGasUsedAndPayFees`: system calls
  route to the system processor and `SkipValidation` excludes them from normal
  header and multidimensional block counters.
- `src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs`:
  system-spec selection, fixed `SystemCall` gas construction, fee/refund/nonce
  overrides, and the effective options used by the block-counter gate.

## Explicit limitations

- This is an observational composition of three handwritten relations. It
  does not prove that the C# adapters refine the Lean adapter, nor that event
  labels are bisimilar to production execution.
- The scalar block state token/full processor-world correspondence is an
  explicit checked premise. Account/storage journals, call frames, tracer
  internals, and `IWorldState` behavior are not derived from the token.
- Deposit-log parsing, request byte encoding, request hashing, addresses,
  calldata construction, target existence/code queries, fork enablement,
  receipt roots, state roots, header hashing, cryptography, and persistence are
  not proved here. The first three are explicit supplied functions or values;
  the remaining facts stay in the imported reference boundaries.
- Request-call validation is performed before admitting the pure `BlockSpec`.
  This proves the observations substituted into its non-failing hooks, not a
  lock-step theorem for exceptions thrown inside production hooks. A request
  failure is therefore represented by the fail-closed outer composition
  result, rather than synthesized as a `BlockReference.Failure`.
- Raw composed `BlockSpec` and `BranchSpec` values are private and are never
  returned by the public API. Public positive results are existential admitted
  runs or projections of `runBlock` / `runValidatedSingletonBranch`; public
  negative theorems show an invalid adapter has no modeled outcome. This is a
  Lean module API boundary, not a language-level capability or trusted-kernel
  security boundary.
- The state-gas-OOG vector uses the imported reference's abstract
  `topFrameOutOfGas` observation with a state-gas-exhaustion settlement. It
  proves fail-closed composition of that observation, not the production VM's
  detection of the cause.
- The admitted branch result covers one synchronous selected block only.
  Per-input adapter validation for a multi-block branch, parallel block
  execution equivalence, background scheduling, database crash durability,
  and concurrent cancellation remain outside this layer.
- Consequently this layer is not evidence that Nethermind, the EVM, or the
  complete mainnet block processor has been formally verified.

## Verification

From `tools/Evm/Lean`, with the pinned Lean toolchain:

```text
lake build Eip803x.BlockSystemComposition Eip803x.BlockSystemCompositionVectors
```

The three files intentionally remain unimported pending independent review and
contain no `sorry`, `admit`, or `axiom` declarations.
