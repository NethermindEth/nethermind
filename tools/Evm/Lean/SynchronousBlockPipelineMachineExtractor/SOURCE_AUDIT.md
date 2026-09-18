# Standard-mainnet synchronous source audit

Status: **audit scaffold only**. `SOURCE_PINS.json` records SHA-256 identities
with `compositionAdmitted: false`; its configuration, specification, and
composition sections also pin the standard mainnet config, its embedded
chain-spec mapping, the chain spec, all handwritten Lean scaffold files, and
eight exact composition bytes. The Roslyn audit is intended to fail closed when
any selected source, route contract, metadata mirror, configuration,
specification, or composition byte changes. No generated kernel or operational
theorem is present. Connecting the pinned EIP-8037 system-call reference to this
route exposed that the EIP-4788 beacon-root call was constructed as a plain
30,000,000-gas transaction. It is now a `SystemCall` with the pinned 31,566,720
total, preserving the 30,000,000 execution budget and supplying the required
1,566,720 state reservoir.

## Concrete route

The standard registrations are in
`src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs`:

- `RecoverSignatures` is the first `IBlockPreprocessorStep`;
- `EthereumTransactionProcessor`, `WorldState`, `EthereumVirtualMachine`,
  `BranchProcessor`, `BlockProcessor`, `WithdrawalProcessor`,
  `ExecutionRequestsProcessor`, `BlockAccessListManager`, and
  `BlockchainProcessor` are registered in the processing scope, together with
  `IBlockValidator`, `IBlockhashProvider`, `IBeaconBlockRootHandler`,
  `IBlockhashStore`, `IInclusionListSatisfactionChecker`,
  `ITransactionProcessorAdapter`, and the reward calculator interfaces;
- the standard validation module registers
  `BlockValidationTransactionsExecutor` and decorates it with
  `ParallelBlockValidationTransactionsExecutor`.

`TransactionProcessorAdapterExtensions.ProcessTransaction` and the
`CreateExecuteAdapter` registration connect that executor to
`ITransactionProcessor`; the audit checks the concrete adapter forwarding, the
`Execute` extension's `ExecutionOptions.Commit` forwarding, the ordinary
processor entry and both terminal paths into `FinalizeTransaction`, without
claiming sender, intrinsic-gas, frame, journal, or settlement semantics.

`src/Nethermind/Nethermind.Init/Modules/MainProcessingContext.cs` creates a
child `BeginLifetimeScope`, injects preprocessors and validation/main modules,
and manually constructs the main `BlockchainProcessor`. `ApiBuilder.LoadChainSpec`
follows the configured `chainspec/foundation.json` through `ChainSpecFileLoader`,
`AutoDetectingChainSpecLoader`, and the Parity `ChainSpecLoader` to the registered
`ChainSpecBasedSpecProvider`; the config, chain-spec bytes,
and `Nethermind.Config.csproj` embedded-resource mapping are pinned. The loader
derives `Nethermind.Config.chainspec.foundation.json` and reads it from the
assembly containing `IConfig`. The static fork markers include
`src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs` at
`AmsterdamBlockTimestamp = ulong.MaxValue - 1`, with `Amsterdam.Instance` bound
to `src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs`; these markers do not
replace the runtime chain-spec derivation. That schedule enables EIP-7778,
EIP-7928, EIP-8037 and EIP-8038. The pinned `foundation.json` does not configure
those target transition properties, so this scaffold does not claim that the
current mainnet config activates Amsterdam.

## Ordered block route

The audit checks these source sites in `BlockProcessor.cs`:

1. `ProcessOne` prepares BAL mode and chooses the system handler, then applies
   DAO transition and `PrepareBlockForProcessing`. BAL preparation runs before
   the processing retry catch boundary; its failures escape that boundary.
2. `ProcessBlock` sets the tracer and execution context, sets up BAL, stores the
   beacon root, applies historical blockhash changes, and commits the
   pre-transaction journal.
3. The executor folds transactions through the ordinary/system transaction
   adapter. On the normal per-transaction path, the audit checks that each loop
   iteration opens its transaction trace, executes through the adapter, builds
   its receipt through the tracer, and closes the trace before the block
   publishes `TransactionsExecuted` and commits the post-transaction journal.
   `BlockReceiptsTracer.BuildReceipt` and
   `BlockReceiptGasAccountingKernel.Accumulate` are the concrete
   settlement-to-paid/cumulative-receipt and two-dimensional block-gas
   boundaries; `SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters`
   is the source gate that excludes system operations from normal counters.
4. Blob gas is calculated; receipt blooms and the receipt root use the updated
   receipts. The source has both synchronous installation and a background
   scheduling/await path. The standard background threshold is at least 16
   receipts or at least 64 logs.
5. Rewards and withdrawals run, followed by the post-system `CommitState`.
   Execution requests consume the block and updated receipts, then block trace
   completion runs.
6. `CommitStateAndStorageRoots`, account-change capture, state-root computation,
   background artifact installation (when selected), and BAL finalization follow.
   The processing header hash is calculated last.
7. `ValidateProcessedBlock` is a caught `InvalidBlockException` boundary; its
   failure disposes account changes. Generic hook failures remain exceptions.
   `StoreTxReceipts` is after successful processed-header validation.

The neutral records keep receipt/status/log fields, cumulative paid gas, the
two block gas dimensions, and header gas as the `EthereumGasPolicy.CombineBlockGas`
maximum, together with requests/BAL observations. They do not
pretend to implement the receipt, state, BAL, request, or header algorithms.

## Branch scope and retry route

`BranchProcessor.Process` opens one owned scope at the parent root, except for
the externally owned null-base genesis case. For a retryable BAL failure it
cancels/clears background work, disposes the failed attempt scope, reopens at
the pre-block base, forces sequential access-list execution, and retains the
second attempt's result or exception. Every successful block observes the
inclusion-list signal, waits for prewarming, invokes `PreCommitBlock`/`CommitTree`,
optionally disposes/reopens at the interior 64-block checkpoint, and resets the
scope for the next block. The branch finally disposes its owned scope. Inclusion
`false` is an observable signal, not an invalid-block rejection.

Parallel success is a separate unproved path. The audit records eligibility,
attempt and retry boundaries but does not assert parallel/sequential
equivalence.

## Outer chain route

`BlockchainProcessor.Process` performs simple suggested-block checks first;
unknown-parent is a skip signal while missing metadata exceptions escape. It
selects a branch, preprocesses every selected block for sender/authority data,
then enters the branch processor. Only `InvalidBlockException` is classified
as an invalid-block result by the production catch boundary; generic exceptions
escape. On a successful branch, total difficulty is assigned before optional
`TryUpdateMainChain`; a false update result is a warning/signal, not rejection,
and marking the chain processed is a distinct later action.

## Header projection

`BlockHeader.CloneForProcessing` constructs a processing header from parent,
uncles, beneficiary, difficulty, number, gas limit, timestamp, and extra data.
Its constructor resets state-root and gas-used defaults; `CopyProcessingFields`
resets bloom to `Core.Bloom.Empty` and preserves author, hash, mix hash, nonce,
transaction root, total difficulty, receipts root, base fee, withdrawals root,
requests hash, post-merge flag, parent beacon root, slot number, BAL hash, blob
gas used, and excess blob gas. This exact roster is an audit obligation, not a
deep-copy or value-semantics proof.

## Catalog and composition integrity

The route has 17 distinct phase labels and 57 distinct hook contracts. Each
hook's source role, input/output shape, failure disposition, receipt and scope
requirements, and logical-state mutation flag is mirrored in
`METADATA_MIRROR.json` and `Specification/MetadataMirror.lean`; the Lean file
contains the SHA-256 of the embedded JSON bytes. The C# audit checks exact phase
membership including multiplicity, hook uniqueness, typed Lean phase pairing,
all 66 unique source-order edges, the exact four-file handwritten Lean roster,
and the cross-language mirror. `BlockSystemComposition` and
`ParallelBlockReference` are accepted only as byte-pinned inputs with their
exact artifact-to-path assignment, not as semantic proof artifacts. The
current static inventory is 50 pinned C# sources, 80 Roslyn symbols, and 182
source anchors/partial-order edges.

The structural sentinels also reject bypassing the concrete execute adapter or
either ordinary transaction terminal path, changing the `Execute` extension
away from commit processing, moving receipt construction off the reachable
finalization path, moving the per-block reset inside the checkpoint condition,
or separating the checkpoint's final dispose/reopen statements. Receipt-task
work must remain inside the scheduled lambda, the synchronous arm must retain
its direct bloom/root statements, and the standard threshold predicate retains
its exact disjunction and comparison polarity.

## Remaining refinement gaps

The open boundaries are concrete: transaction/frame/world-journal semantics;
system-operation bodies; precompiles/cryptography; settlement widths; receipt,
state, trie/RLP and header hashing; BAL and parallel equivalence; scope/journal
rollback; `CommitTree` durability and persistence; background scheduling and
exception precedence; DI/virtual dispatch and CLR/JIT/compiler correctness;
and head/processed-chain persistence. Closing the typed route metadata alone
does not justify saying that Nethermind, the entire EVM, or the mainnet block
processor is formally verified.
