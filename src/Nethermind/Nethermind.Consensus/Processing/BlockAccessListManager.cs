// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using System.Collections.Concurrent;
using Nethermind.Core.Caching;
using Nethermind.Core.Cpu;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Specs;
using static Nethermind.Consensus.Processing.BlockProcessor;
using static Nethermind.State.BlockAccessListBasedWorldState;
using System.Threading;

namespace Nethermind.Consensus.Processing;

public class BlockAccessListManager(
    IWorldState stateProvider,
    ISpecProvider specProvider,
    IBlockhashProvider blockHashProvider,
    ILogManager logManager,
    IBlocksConfig blocksConfig,
    IWithdrawalProcessorFactory withdrawalProcessorFactory)
    : IBlockAccessListManager
{
    private BlockExecutionContext? _blockExecutionContext;
    private ITxProcessorWithWorldStateManager? _txProcessorWithWorldStateManager;
    private readonly Lazy<ParallelTxProcessorWithWorldStateManager> _parallelTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager));
    private readonly Lazy<SequentialTxProcessorWithWorldStateManager> _sequentialTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager));
    private const int GasValidationChunkSize = 8;
    private long? _gasRemaining;
    private bool _isBuilding;
    private bool _blockAccessListsEnabled;
    // Cache key guarding LoadPreStateToSuggestedBlockAccessList against double-mutation of the
    // suggested block's BAL: that method appends prestate entries in place, so calling it
    // twice for the same Block instance corrupts the BAL. PrepareForProcessing can be invoked
    // more than once per block within the same DI scope (the manager is scoped to the main
    // processing context, not per block) — e.g. on retry — so we skip the load when the hash
    // matches the most recently loaded one.
    private Hash256 _lastLoadedBal = Hash256.Zero;

    // Column-oriented validation index used by the fast path in ValidateBlockAccessList. The
    // suggested index is built once at PrepareForProcessing; the generated index mirrors its
    // layout and is appended-to per-tx from MergeAndReturnBal. Equality at a given tx index
    // collapses to row-aligned span compares — no per-account dictionary lookups, no per-tx
    // walks over the whole BAL. Null when BlockAccessList is unset (e.g. pre-Amsterdam).
    private BlockAccessListValidationIndex? _suggestedValidationIndex;
    private BlockAccessListValidationIndex? _generatedValidationIndex;
    // Total chargeable storage reads on each side. Suggested is computed once at Build time;
    // generated is incremented as each per-tx slice merges in. ValidateBlockAccessList's
    // fast path checks (suggestedReads - generatedReads) * Eip7928Constants.ItemCost against
    // _gasRemaining instead of re-walking the whole BAL.
    private int _suggestedChargeableStorageReads;
    private int _generatedChargeableStorageReads;
    private bool _hasGeneratedValidationIndexUpdates;

    public class ParallelExecutionException(InvalidBlockException innerException)
        : InvalidTransactionException(
            innerException.InvalidBlock,
            innerException.Message,
            (innerException as InvalidTransactionException)?.Reason
                ?? TransactionResult.ErrorType.MalformedTransaction.WithDetail($"Parallel execution failure: {innerException.Message}"),
            innerException);
    public GeneratedBlockAccessList GeneratedBlockAccessList { get; set; } = new();
    public bool Enabled { get; private set; }
    public bool ParallelExecutionEnabled { get; private set; }

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
    {
        _blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;
        Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;
        _isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock);

        // Parallel execution requires the BAL body to be present on the block.
        // Blocks from p2p/RLP fixtures only have the header hash, not the decoded BAL body.
        ParallelExecutionEnabled = Enabled
            && blocksConfig.ParallelExecution
            && !_isBuilding
            && suggestedBlock.BlockAccessList is not null;

        if (Enabled)
        {
            Reset();
            _gasRemaining = suggestedBlock.GasUsed;

            // See _lastLoadedBal field comment — skip when the same block is re-prepared.
            // Prestate loading itself is now deferred to slot 0 of the parallel For loop so
            // worker threads can start executing transactions while the loader proceeds. Here
            // we only flip the per-account gates so any worker that races ahead of the loader
            // blocks until prestate for its account has been loaded.
            if (ParallelExecutionEnabled && suggestedBlock.Hash != _lastLoadedBal)
            {
                foreach (ReadOnlyAccountChanges accountChanges in suggestedBlock.BlockAccessList.AccountChanges)
                {
                    accountChanges.EnablePrestateGate();
                }
            }

            // Build the column-oriented validation index used by ValidateBlockAccessList's
            // fast path. Runs once per block; subsequent per-tx ChangesEqual calls become
            // row-aligned span compares. Tally suggested-side chargeable storage reads here
            // so the per-tx surplus-reads gas check can avoid re-walking the BAL.
            if (suggestedBlock.BlockAccessList is not null)
            {
                BlockAccessListValidationIndex.AddressIndex addressIndex = new();
                _suggestedValidationIndex = BlockAccessListValidationIndex.Build(suggestedBlock.BlockAccessList, suggestedBlock.Transactions.Length, addressIndex);
                _generatedValidationIndex = new(suggestedBlock.Transactions.Length, addressIndex, _suggestedValidationIndex);
                int suggestedReads = 0;
                foreach (ReadOnlyAccountChanges ac in suggestedBlock.BlockAccessList.AccountChanges)
                {
                    suggestedReads += IsSystemContract(ac.Address) ? 0 : ac.StorageReads.Length;
                }
                _suggestedChargeableStorageReads = suggestedReads;
            }
        }
    }

    public void Setup(Block block)
    {
        if (Enabled)
        {
            _txProcessorWithWorldStateManager = ParallelExecutionEnabled ? _parallelTxProcessorWithWorldStateManager.Value : _sequentialTxProcessorWithWorldStateManager.Value;
            CheckInitialized();
            _txProcessorWithWorldStateManager.Setup(block, _blockExecutionContext.Value);
        }
    }

    public void SpendGas(long gas)
    {
        CheckInitialized();
        _gasRemaining -= gas;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        => _blockExecutionContext = blockExecutionContext;

    public ITransactionProcessorAdapter GetTxProcessor(uint? balIndex = null)
    {
        CheckInitialized();
        return _txProcessorWithWorldStateManager.Get(balIndex).TxProcessorAdapter;
    }

    public void NextTransaction()
    {
        if (Enabled)
        {
            CheckInitialized();
            _txProcessorWithWorldStateManager.Get().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
            _txProcessorWithWorldStateManager.NextTransaction();
        }
    }

    public void Rollback()
    {
        if (Enabled)
        {
            CheckInitialized();
            _txProcessorWithWorldStateManager.Rollback();
        }
    }

    public void ReturnTxProcessor(uint balIndex)
    {
        if (Enabled && ParallelExecutionEnabled)
        {
            // Eagerly detach the worker's generated BAL into a per-tx slot and recycle the
            // pool slot. Workers therefore never block on the validator — but the validator
            // still merges per-tx slots into the target in order, preserving incremental
            // validation semantics.
            _parallelTxProcessorWithWorldStateManager.Value.Return(balIndex);
        }
    }

    public void IncrementalValidation(Block block, GasValidationResultSlot[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, Task preExecutionTask, CancellationToken token)
    {
        CheckInitialized();

        int len = block.Transactions.Length;

        // Pre is held by main-thread system contract handlers — never went through Return,
        // so MergeAndReturnBal will detach it before merging.
        preExecutionTask.GetAwaiter().GetResult();
        _txProcessorWithWorldStateManager.MergeAndReturnBal(0u, GeneratedBlockAccessList, RegisterGeneratedSlice);
        ValidateBlockAccessList(block, 0u);

        long totalRegularGas = 0;
        long totalStateGas = 0;
        for (int chunkStart = 0; chunkStart < len; chunkStart += GasValidationChunkSize)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            int chunkEnd = Math.Min(chunkStart + GasValidationChunkSize, len);
            for (int j = chunkStart; j < chunkEnd; j++)
            {
                Transaction tx = block.Transactions[j];

                GasValidationResult gasResult = gasResults[j].GetResult();
                IntrinsicGas<EthereumGasPolicy> intrinsicGas = gasResult.IntrinsicGas;
                // EIP-8037 per-tx 2D inclusion check (execution-specs PR 2703).
                // totalRegularGas/totalStateGas reflect the cumulatives BEFORE this tx;
                // the worst-case per-dimension contribution must fit the remaining budget.
                // The worker precomputes intrinsic gas once and carries it here to avoid
                // recalculating dynamic state-byte costs on the validation thread.
                CheckPerTxInclusion(block, j, tx, _blockExecutionContext.Value.Spec, totalRegularGas, totalStateGas, in intrinsicGas);

                // Surface the worker's original tx-rejection reason before running any
                // downstream gas accounting. Otherwise CheckGasUsed can mask the true cause,
                // unlike the sequential path, which never reaches accounting on a rejected tx.
                if (gasResult.Exception is not null)
                    throw new ParallelExecutionException(gasResult.Exception);

                totalRegularGas += gasResult.BlockGasUsed;
                totalStateGas += gasResult.BlockStateGasUsed;
                SpendGas(gasResult.BlockGasUsed);

                CheckGasUsed(j, block, totalRegularGas, totalStateGas);

                transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(j, block.Transactions[j], block.Header, receiptsTracers[j].TxReceipts[0]));

                // Worker for tx (j+1) has stashed its BAL into _perTxBal[j+1] via Return as
                // soon as the tx finished — no contention with the validator. Merge it into
                // the target now, in order, so incremental validation sees only data from
                // txs 0..(j+1).
                bool validateStorageReads = j == chunkEnd - 1;
                _txProcessorWithWorldStateManager.MergeAndReturnBal((uint)(j + 1), GeneratedBlockAccessList, RegisterGeneratedSlice);
                ValidateBlockAccessList(block, (uint)(j + 1), validateStorageReads);
            }
        }

        // EIP-8037: 2D gas accounting — block gasUsed = max(sum_regular, sum_state)
        _blockExecutionContext.Value.Header.GasUsed = Math.Max(totalRegularGas, totalStateGas);

        static void CheckGasUsed(int index, Block block, long totalRegularGas, long totalStateGas)
        {
            // EIP-8037: block gasUsed = max(sum_regular, sum_state)
            long effectiveGas = Math.Max(totalRegularGas, totalStateGas);
            if (effectiveGas > block.Header.GasLimit)
            {
                throw new InvalidBlockException(block, $"Block gas limit exceeded: cumulative gas {effectiveGas} > block gas limit {block.Header.GasLimit} after transaction index {index}.");
            }
        }

    }

    internal static void CheckPerTxInclusion(Block block, int index, Transaction tx, IReleaseSpec spec, long cumulativeRegular, long cumulativeState)
    {
        // EIP-8037 (bal-devnet-6, execution-specs PR 2703): worst-case 2D inclusion
        // check. Only applies when EIP-8037 is active; legacy and pre-EIP-8037 blocks
        // continue to rely solely on the post-execution running max(R,S) check.
        if (!spec.IsEip8037Enabled) return;

        IntrinsicGas<EthereumGasPolicy> intrinsic = EthereumGasPolicy.CalculateIntrinsicGas(tx, spec, block.Header.GasLimit);
        CheckPerTxInclusion(block, index, tx, spec, cumulativeRegular, cumulativeState, in intrinsic);
    }

    internal static void CheckPerTxInclusion(Block block, int index, Transaction tx, IReleaseSpec spec, long cumulativeRegular, long cumulativeState, in IntrinsicGas<EthereumGasPolicy> intrinsic)
    {
        // EIP-8037 (bal-devnet-6, execution-specs PR 2703): worst-case 2D inclusion
        // check. Only applies when EIP-8037 is active; legacy and pre-EIP-8037 blocks
        // continue to rely solely on the post-execution running max(R,S) check.
        if (!spec.IsEip8037Enabled) return;

        long intrinsicRegular = intrinsic.Standard.Value;
        long intrinsicState = intrinsic.Standard.StateReservoir;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            block.Header.GasLimit,
            cumulativeRegular,
            cumulativeState,
            tx.GasLimit,
            intrinsicRegular,
            intrinsicState);

        if (outcome != Eip8037BlockGasInclusionCheck.Outcome.Ok)
        {
            throw new InvalidBlockException(block,
                $"Block gas limit exceeded: tx {index} fails EIP-8037 inclusion check ({outcome}); " +
                $"regular_available={block.Header.GasLimit - cumulativeRegular}, " +
                $"state_available={block.Header.GasLimit - cumulativeState}, " +
                $"tx.gas={tx.GasLimit}, intrinsic.regular={intrinsicRegular}, intrinsic.state={intrinsicState}.");
        }
    }

    /// <summary>
    /// Applies the suggested-block BAL deltas onto <paramref name="stateProvider"/> so the post-block
    /// world state matches the wire BAL.
    /// </summary>
    /// <remarks>
    /// Requires <paramref name="suggestedBlockAccessList"/> to have already been prestate-loaded
    /// via <see cref="LoadPreStateToSuggestedBlockAccessList"/>. The <c>oldBalance</c> calculation
    /// below relies on a prestate balance entry being present (it falls back to zero only for
    /// brand-new accounts created in this block). Callers in the parallel and sequential paths
    /// run prestate load as the first step; do not invoke this on a freshly-decoded BAL.
    /// </remarks>
    public static void ApplyStateChanges(ReadOnlyBlockAccessList suggestedBlockAccessList, IWorldState stateProvider, IReleaseSpec spec, bool shouldComputeStateRoot)
    {
        foreach (ReadOnlyAccountChanges accountChanges in suggestedBlockAccessList.AccountChanges)
        {
            if (accountChanges.BalanceChanges.Length > 0 && accountChanges.BalanceChanges[^1].Index != Eip7928Constants.PrestateIndex)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                // GetBalance(0) returns the prestate entry (the only change with index < 0 in the
                // ordering used by PrestateAwareIndexComparer). Null only for accounts created
                // mid-block — those have no prestate balance, so 0 is the correct delta base.
                UInt256 oldBalance = accountChanges.GetBalance(0) ?? UInt256.Zero;
                UInt256 newBalance = accountChanges.BalanceChanges[^1].Value;
                if (newBalance > oldBalance)
                {
                    stateProvider.AddToBalance(accountChanges.Address, newBalance - oldBalance, spec);
                }
                else
                {
                    stateProvider.SubtractFromBalance(accountChanges.Address, oldBalance - newBalance, spec);
                }
            }

            if (accountChanges.NonceChanges.Length > 0 && accountChanges.NonceChanges[^1].Index != Eip7928Constants.PrestateIndex)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                stateProvider.SetNonce(accountChanges.Address, accountChanges.NonceChanges[^1].Value);
            }

            if (accountChanges.CodeChanges.Length > 0 && accountChanges.CodeChanges[^1].Index != Eip7928Constants.PrestateIndex)
            {
                stateProvider.InsertCode(accountChanges.Address, accountChanges.CodeChanges[^1].Code, spec);
            }

            foreach (ReadOnlySlotChanges slotChange in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, slotChange.Key);
                // could be empty since prestate loaded
                int slotCount = slotChange.Changes.Length;
                if (slotCount > 0 && slotChange.Changes[^1].Index != Eip7928Constants.PrestateIndex)
                {
                    // StorageChange.Value is now EvmWord (Vector256<byte>) in big-endian wire form.
                    EvmWord value = slotChange.Changes[^1].Value;
                    ReadOnlySpan<byte> valueBytes = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
                        ref Unsafe.As<EvmWord, byte>(ref value), 32);
                    stateProvider.Set(storageCell, [.. valueBytes.WithoutLeadingZeros()]);
                }
            }
        }
        stateProvider.Commit(spec);
        if (shouldComputeStateRoot)
        {
            stateProvider.RecalculateStateRoot();
        }
    }

    public void ApplyAuRaPreprocessingChanges(IReleaseSpec spec, Address withdrawalContractAddress)
    {
        if (!Enabled)
        {
            return;
        }

        stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero, UInt256.Zero);
        stateProvider.CreateAccount(withdrawalContractAddress, UInt256.Zero, UInt256.Zero);
        stateProvider.Commit(spec.ForSystemTransaction(true, false), commitRoots: false);
    }

    public void SetBlockAccessList(Block block)
    {
        if (!_blockAccessListsEnabled)
        {
            return;
        }

        if (block.IsGenesis)
        {
            block.Header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
        }
        else
        {
            CheckInitialized();

            _txProcessorWithWorldStateManager.MergeAndReturnBal(uint.MaxValue, GeneratedBlockAccessList);
            block.GeneratedBlockAccessList = GeneratedBlockAccessList;
            block.EncodedBlockAccessList = Rlp.Encode(GeneratedBlockAccessList).Bytes;
            block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
        }
    }

    public void ValidateBlockAccessList(Block block, uint index, bool validateStorageReads = true)
    {
        if (block.BlockAccessList is null)
        {
            return;
        }

        CheckInitialized();

        // Fast path: when the column-oriented validation index is populated for this index,
        // a single ChangesEqual call compares both sides row-by-row in bulk. On match, only
        // the surplus-storage-reads gas check remains — no per-account dict lookups, no
        // sorted merge walk. On mismatch (or when the index isn't ready), fall through to
        // the streaming walk below which produces precise diagnostics.
        if (_hasGeneratedValidationIndexUpdates &&
            _suggestedValidationIndex is not null &&
            _generatedValidationIndex is not null &&
            _generatedValidationIndex.ChangesEqual(_suggestedValidationIndex, index))
        {
            int fastSurplus = _suggestedChargeableStorageReads - _generatedChargeableStorageReads;
            if (validateStorageReads && fastSurplus > 0 && _gasRemaining < fastSurplus * Eip7928Constants.ItemCost)
            {
                throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
            }
            return;
        }

        GeneratedBlockAccessList generated = GeneratedBlockAccessList;
        ReadOnlyBlockAccessList suggested = block.BlockAccessList;

        int generatedReads = 0;
        int suggestedReads = 0;

        // Pass 1: walk generated; for each account, look up the matching entry in suggested
        // via the dictionary (O(1)) instead of a sorted merge-walk. Catches "missing-from-
        // suggested" and "incorrect changes at this index".
        foreach (GeneratedAccountChanges gen in generated.AccountChanges)
        {
            int genReads = IsSystemContract(gen.Address) ? 0 : gen.StorageReads.Count;
            generatedReads += genReads;

            ReadOnlyAccountChanges? sug = suggested.GetAccountChanges(gen.Address);
            if (sug is not null)
            {
                if (!gen.ChangesAtIndexEqual(sug, index))
                {
                    throw new InvalidBlockLevelAccessListException(block.Header,
                        $"Suggested block-level access list contained incorrect changes for {gen.Address} at index {index}.");
                }
                continue;
            }

            // Generated has the account, suggested doesn't. Tolerated only when there are no
            // changes at this index AND the entry is either a system-user read at index 0 or
            // a generic storage-read-only entry.
            if (gen.HasNoChangesAtIndex(index) &&
                ((index == 0 && gen.Address == Address.SystemUser && genReads == 0) || genReads > 0))
            {
                continue;
            }

            throw new InvalidBlockLevelAccessListException(block.Header,
                $"Suggested block-level access list missing account changes for {gen.Address} at index {index}.");
        }

        // Pass 2: walk suggested; only accounts NOT present in generated need attention.
        // Tally suggested reads here for the storage-read gas-budget check below.
        foreach (ReadOnlyAccountChanges sug in suggested.AccountChanges)
        {
            suggestedReads += IsSystemContract(sug.Address) ? 0 : sug.StorageReads.Length;

            if (generated.HasAccount(sug.Address))
            {
                continue;
            }

            if (!sug.HasNoChangesAtIndex(index))
            {
                throw new InvalidBlockLevelAccessListException(block.Header,
                    $"Suggested block-level access list contained surplus changes for {sug.Address} at index {index}.");
            }
        }

        int surplusSuggestedReads = suggestedReads - generatedReads;
        if (validateStorageReads && surplusSuggestedReads > 0 && _gasRemaining < surplusSuggestedReads * Eip7928Constants.ItemCost)
        {
            throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
        }
    }

    /// <summary>
    /// Hook called by <see cref="ITxProcessorWithWorldStateManager.MergeAndReturnBal"/> after
    /// each per-tx slice merges into the cumulative <see cref="GeneratedBlockAccessList"/>.
    /// Pushes the slice's rows into <see cref="_generatedValidationIndex"/> so the next
    /// <see cref="ValidateBlockAccessList"/> call at this index can take the fast path, and
    /// rolls the chargeable-storage-reads counter forward.
    /// </summary>
    private void RegisterGeneratedSlice(BlockAccessListAtIndex slice)
    {
        if (_generatedValidationIndex is null)
        {
            return;
        }

        _generatedValidationIndex.Add(slice);
        foreach (AccountChangesAtIndex ac in slice.AccountChanges)
        {
            if (!IsSystemContract(ac.Address))
            {
                _generatedChargeableStorageReads += ac.StorageReads.Count;
            }
        }
        _hasGeneratedValidationIndexUpdates = true;
    }

    private static bool IsSystemContract(Address address)
        => address == Eip7002Constants.WithdrawalRequestPredeployAddress
        || address == Eip7251Constants.ConsolidationRequestPredeployAddress;

    public void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        new BeaconBlockRootHandler(preExecution.TxProcessor, preExecution.WorldState).StoreBeaconRoot(block, spec, NullTxTracer.Instance);
    }

    public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        new BlockhashStore(preExecution.WorldState).ApplyBlockhashStateChanges(header, spec);
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState postExecution = _txProcessorWithWorldStateManager.GetPostExecution();
        IWithdrawalProcessor withdrawalProcessor = withdrawalProcessorFactory.Create(postExecution.WorldState, postExecution.TxProcessor);
        if (_isBuilding)
        {
            withdrawalProcessor = new BlockProductionWithdrawalProcessor(withdrawalProcessor);
        }
        withdrawalProcessor.ProcessWithdrawals(block, spec);
    }

    public void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState postExecution = _txProcessorWithWorldStateManager.GetPostExecution();
        new ExecutionRequestsProcessor(postExecution.TxProcessor).ProcessExecutionRequests(block, postExecution.WorldState, txReceipts, spec);
    }

    public void LoadPreStateToSuggestedBlockAccessList(Block suggestedBlock)
    {
        if (!ParallelExecutionEnabled || suggestedBlock.BlockAccessList is null) return;

        // Skip if this exact BAL was already loaded — see _lastLoadedBal field comment. The
        // workers' gates were also skipped in PrepareForProcessing in that case, so nothing
        // to signal here either.
        if (suggestedBlock.Hash == _lastLoadedBal) return;
        _lastLoadedBal = suggestedBlock.Hash;

        // Wire BAL validation must run before this method: it appends local-only
        // PrestateIndex entries that are sorted before real tx indices and must not be
        // subjected to block-level wire index-bounds validation.
        ReadOnlyBlockAccessList bal = suggestedBlock.BlockAccessList;
        try
        {
            foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
            {
                // record whether the account was modified before any prestate is added
                accountChanges.RecordWasChanged();

                bool exists = stateProvider.TryGetAccount(accountChanges.Address, out AccountStruct account);
                accountChanges.SetExistedBeforeBlock(exists);
                accountChanges.SetEmptyBeforeBlock(!account.HasStorage);

                accountChanges.LoadPreStateBalance(account.Balance);
                accountChanges.LoadPreStateNonce((ulong)account.Nonce);
                accountChanges.LoadPreStateCode(stateProvider.GetCode(accountChanges.Address) ?? []);

                // snapshot keys to avoid modifying the slot collection during iteration
                // (LoadPreStateStorage can insert a new ReadOnlySlotChanges for a previously read-only slot)
                UInt256[] slotsToLoad = [.. accountChanges.GetSlotsForPreStateLoad()];
                foreach (UInt256 slot in slotsToLoad)
                {
                    StorageCell storageCell = new(accountChanges.Address, slot);
                    UInt256 value = new(stateProvider.Get(storageCell), true);
                    accountChanges.LoadPreStateStorage(slot, value);
                }

                // Signal as soon as this account is fully loaded — workers blocked on it via
                // ReadOnlyAccountChanges.WaitForPrestate can proceed without waiting for the
                // remaining accounts to finish.
                accountChanges.SignalPrestateLoaded();
            }
        }
        catch
        {
            // If the loader throws partway, release every remaining gate so workers and the
            // incremental validator don't hang. Already-signaled gates are no-ops; not-yet-
            // loaded accounts will return their pre-load (empty / default) state, which will
            // surface as an InvalidBlockLevelAccessListException on the worker — preferable
            // to a deadlock. The original exception still propagates from slot 0.
            foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
            {
                accountChanges.SignalPrestateLoaded();
            }
            throw;
        }
    }

    private void CheckInitialized()
    {
        if (_txProcessorWithWorldStateManager is null) ThrowNotInitialized(nameof(_txProcessorWithWorldStateManager));
        if (_gasRemaining is null) ThrowNotInitialized(nameof(_gasRemaining));
        if (_blockExecutionContext is null) ThrowNotInitialized(nameof(_blockExecutionContext));
    }

    private void Reset()
    {
        _txProcessorWithWorldStateManager = null;
        _blockExecutionContext = null;
        _gasRemaining = null;
        GeneratedBlockAccessList.Reset();
        _suggestedValidationIndex = null;
        _generatedValidationIndex = null;
        _suggestedChargeableStorageReads = 0;
        _generatedChargeableStorageReads = 0;
        _hasGeneratedValidationIndexUpdates = false;
    }

    [DoesNotReturn]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");

    private interface ITxProcessorWithWorldStateManager
    {
        void Setup(Block block, BlockExecutionContext blockExecutionContext);
        TxProcessorWithWorldState Get(uint? balIndex = null);
        TxProcessorWithWorldState GetPreExecution() => Get(0u);
        TxProcessorWithWorldState GetPostExecution() => Get(uint.MaxValue);
        void NextTransaction();
        void Rollback();
        void MergeAndReturnBal(uint balIndex, GeneratedBlockAccessList target, Action<BlockAccessListAtIndex>? onSlice = null);
    }

    private class ParallelTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager
    {
        private const int DefaultTxCount = 10000;
        private static readonly int ProcessorPoolSize = RuntimeInformation.ProcessorCount;

        // BAL pool is larger since extra BALs are retained so they can be merged in order
        private static readonly int BalPoolSize = RuntimeInformation.ProcessorCount * 2;

        static ParallelTxProcessorWithWorldStateManager()
        {
            StaticPool<BlockAccessListAtIndex>.SetMaxPooledCount(BalPoolSize);
            for (int i = 0; i < BalPoolSize; i++)
            {
                StaticPool<BlockAccessListAtIndex>.Return(new());
            }
        }

        private Block? _currentBlock;
        private BlockExecutionContext _currentCtx;
        private int _lastBalIndex;

        // _inUse[i] is the processor currently bound to balIndex i.
        private TxProcessorWithWorldState?[] _inUse = new TxProcessorWithWorldState?[DefaultTxCount];

        // _perTxBal[i] holds its detached BAL until the validator merges it in order.
        private BlockAccessListAtIndex?[] _perTxBal = new BlockAccessListAtIndex?[DefaultTxCount];

        // processors are not shared statically between BAL managers
        private readonly ConcurrentQueue<TxProcessorWithWorldState> _processors = [];
        private readonly IBlockhashProvider _blockHashProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IWorldState _stateProvider;
        private readonly ILogManager _logManager;
        private int _processorCount;

        public ParallelTxProcessorWithWorldStateManager(
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager)
        {
            _blockHashProvider = blockHashProvider;
            _specProvider = specProvider;
            _stateProvider = stateProvider;
            _logManager = logManager;
            for (int i = 0; i < ProcessorPoolSize; i++)
            {
                _processors.Enqueue(NewProcessor());
                _processorCount++;
            }
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext)
        {
            _currentBlock = block;
            _currentCtx = blockExecutionContext;
            int previousSize = _lastBalIndex + 1;
            int newLastBalIndex = block.Transactions.Length + 1;
            ReclaimAndResize(newLastBalIndex + 1, previousSize);
            _lastBalIndex = newLastBalIndex;
        }

        // Thread-safety note for _inUse / _perTxBal:
        //   Each balIndex slot has at most one writer at a time. Pre/post (idx 0 and
        //   _lastBalIndex) are written only by the main thread; tx slots (1..len) are
        //   each owned by a single parallel-loop iteration, so no two workers ever
        //   touch the same slot. Cross-thread reads (validator → worker's slot) happen
        //   strictly after the worker's gasResults[i-1].SetResult, whose pairing with
        //   GetResult() establishes the publication barrier. Plain reads/writes are
        //   therefore sufficient — Volatile/Interlocked would be redundant fencing.
        public TxProcessorWithWorldState Get(uint? balIndex = null)
        {
            if (_currentBlock is null) ThrowNotInitialized(nameof(_currentBlock));

            int idx = ClampBalIndex(balIndex ?? 0u);

            // Re-entrant Get for the same balIndex returns the already-acquired processor
            // (lets pre/post callers share state across calls — main thread only).
            TxProcessorWithWorldState? existing = _inUse[idx];
            if (existing is not null) return existing;

            TxProcessorWithWorldState processor = RentProcessor();

            // Install a fresh BAL before Setup so the worker has somewhere to record changes.
            processor.WorldState.SetGeneratingBlockAccessList(StaticPool<BlockAccessListAtIndex>.Rent());
            processor.Setup(_currentBlock, _currentCtx, (uint)idx);
            _inUse[idx] = processor;
            return processor;
        }

        /// <summary>Detaches the worker's populated BAL into the per-tx slot and recycles
        /// the processor immediately, so workers never block on the validator.</summary>
        public void Return(uint balIndex)
        {
            int idx = ClampBalIndex(balIndex);

            TxProcessorWithWorldState? processor = _inUse[idx];
            if (processor is null) return;

            _perTxBal[idx] = processor.WorldState.GetGeneratingBlockAccessList();
            processor.WorldState.SetGeneratingBlockAccessList(null);
            _inUse[idx] = null;
            ReturnProcessor(processor);
        }

        /// <summary>Merges the per-tx BAL into <paramref name="target"/> in caller-controlled
        /// order, then returns it to the pool. Idempotent w.r.t. <see cref="Return"/>: also
        /// detaches the BAL for pre/post callers that never went through Return. The optional
        /// <paramref name="onSlice"/> hook (used by <see cref="BlockAccessListValidationIndex"/>)
        /// runs after the merge but before the slice goes back to the pool, so the manager can
        /// snapshot per-tx rows for the validator's fast path.</summary>
        public void MergeAndReturnBal(uint balIndex, GeneratedBlockAccessList target, Action<BlockAccessListAtIndex>? onSlice = null)
        {
            int idx = ClampBalIndex(balIndex);

            Return((uint)idx);

            BlockAccessListAtIndex? source = _perTxBal[idx];
            if (source is null) return;

            target.Merge(source);
            onSlice?.Invoke(source);
            _perTxBal[idx] = null;
            StaticPool<BlockAccessListAtIndex>.Return(source);
        }

        public void NextTransaction() { }

        public void Rollback() { }

        private int ClampBalIndex(uint balIndex)
            => (int)uint.Min(balIndex, (uint)_lastBalIndex);

        private TxProcessorWithWorldState NewProcessor()
            => new(true, _blockHashProvider, _specProvider, _stateProvider, _logManager);

        private TxProcessorWithWorldState RentProcessor()
        {
            if (Volatile.Read(ref _processorCount) > 0 && _processors.TryDequeue(out TxProcessorWithWorldState? p))
            {
                Interlocked.Decrement(ref _processorCount);
                return p;
            }
            return NewProcessor();
        }

        private void ReturnProcessor(TxProcessorWithWorldState p)
        {
            if (Interlocked.Increment(ref _processorCount) > ProcessorPoolSize)
            {
                Interlocked.Decrement(ref _processorCount);
                return;
            }
            _processors.Enqueue(p);
        }

        private void ReclaimAndResize(int size, int previousSize)
        {
            for (int i = 0; i < previousSize; i++)
                if (_inUse[i] is not null) Return((uint)i);

            for (int i = 0; i < previousSize; i++)
            {
                if (_perTxBal[i] is { } bal)
                {
                    StaticPool<BlockAccessListAtIndex>.Return(bal);
                    _perTxBal[i] = null;
                }
            }

            if (_inUse.Length < size)
                Array.Resize(ref _inUse, Math.Max(2 * _inUse.Length, size));
            if (_perTxBal.Length < size)
                Array.Resize(ref _perTxBal, Math.Max(2 * _perTxBal.Length, size));
        }

    }

    private class SequentialTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager
    {
        private readonly TxProcessorWithWorldState _txProcessorWithWorldState;

        public SequentialTxProcessorWithWorldStateManager(
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager)
        {
            _txProcessorWithWorldState = new(false, blockHashProvider, specProvider, stateProvider, logManager);
            _txProcessorWithWorldState.WorldState.SetGeneratingBlockAccessList(new());
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext)
            => _txProcessorWithWorldState.Setup(block, blockExecutionContext, 0u);

        public TxProcessorWithWorldState Get(uint? _)
            => _txProcessorWithWorldState;

        public void NextTransaction()
        {
            _txProcessorWithWorldState.WorldState.Clear();
            _txProcessorWithWorldState.WorldState.IncrementIndex();
        }

        public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();

        public void MergeAndReturnBal(uint _, GeneratedBlockAccessList target, Action<BlockAccessListAtIndex>? onSlice = null)
        {
            _txProcessorWithWorldState.WorldState.MergeGeneratingBal(target);
            // Sequential path keeps the per-tx slice alive on the worldstate; expose it to
            // the validator-fast-path hook so the validation index sees the same per-tx rows
            // as the parallel path would.
            if (onSlice is not null)
            {
                BlockAccessListAtIndex? slice = _txProcessorWithWorldState.WorldState.GetGeneratingBlockAccessList();
                if (slice is not null) onSlice(slice);
            }
        }
    }

    private class TxProcessorWithWorldState
    {
        public readonly TracedAccessWorldState WorldState;
        public readonly TransactionProcessor<EthereumGasPolicy> TxProcessor;
        public readonly ExecuteTransactionProcessorAdapter TxProcessorAdapter;
        private readonly BlockAccessListBasedWorldState? _balWorldState;

        public TxProcessorWithWorldState(
            bool parallel,
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager)
        {

            VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
            IWorldState worldState = stateProvider;
            if (parallel)
            {
                _balWorldState = new BlockAccessListBasedWorldState(stateProvider, logManager);
                worldState = _balWorldState;
            }
            WorldState = new TracedAccessWorldState(worldState, parallel);
            EthereumCodeInfoRepository codeInfoRepository = new(WorldState);
            TxProcessor = new(BlobBaseFeeCalculator.Instance, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager, parallel);
            TxProcessorAdapter = new(TxProcessor);
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex)
        {
            WorldState.Clear();
            WorldState.SetIndex(balIndex);
            _balWorldState?.SetBlockAccessIndex(balIndex);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
            _balWorldState?.Setup(block);
        }
    }
}
