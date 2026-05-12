// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Coordinates all BAL-aware processing for a block: spinning up the right tx-processor pool
/// (parallel vs sequential), publishing the cumulative <see cref="GeneratedBlockAccessList"/>,
/// and orchestrating prestate load → tx execution → incremental validation → state apply.
/// Implementation is split across partial files by concern:
///   * BlockAccessListManager.cs                       — lifecycle, per-tx hot path, fields
///   * BlockAccessListManager.Validation.cs            — incremental + per-tx 2D inclusion check
///   * BlockAccessListManager.PrestateAndStateChanges.cs — prestate load, ApplyStateChanges, SetBlockAccessList
///   * BlockAccessListManager.SystemContracts.cs       — beacon root, blockhash, AuRa, withdrawals, requests
///   * BlockAccessListManager.TxProcessorPool.cs       — nested pool / processor / world-state types
/// </summary>
public partial class BlockAccessListManager(
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
}
