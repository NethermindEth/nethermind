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

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Coordinates all BAL-aware processing for a block: spinning up the right tx-processor pool
/// (parallel vs sequential), publishing the cumulative <see cref="GeneratedBlockAccessList"/>,
/// and orchestrating tx execution → incremental validation → state apply.
/// Implementation is split across partial files by concern:
///   * BlockAccessListManager.cs                       — lifecycle, per-tx hot path, fields
///   * BlockAccessListManager.Validation.cs            — incremental + per-tx 2D inclusion check
///   * BlockAccessListManager.StateChanges.cs          — ApplyStateChanges, SetBlockAccessList
///   * BlockAccessListManager.SystemContracts.cs       — beacon root, blockhash, AuRa, withdrawals, requests
///   * BlockAccessListManager.TxProcessorPool.cs       — nested pool / processor / world-state types
/// </summary>
/// <remarks>
/// Parent-state fallbacks (for slots the suggested BAL doesn't cover) flow through a pooled
/// <see cref="IReadOnlyTxProcessingEnvFactory"/>: each parallel worker rents a snapshot scoped
/// to the state root captured in <see cref="PrepareForProcessing"/>. Passing <c>null</c>
/// disables this and thus parallel execution.
/// </remarks>
public partial class BlockAccessListManager(
    IWorldState stateProvider,
    ISpecProvider specProvider,
    IBlockhashProvider blockHashProvider,
    ILogManager logManager,
    IBlocksConfig blocksConfig,
    IWithdrawalProcessorFactory withdrawalProcessorFactory,
    PrewarmerEnvFactory? prewarmerEnvFactory = null,
    PreBlockCaches? preBlockCaches = null,
    IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory = null)
    : IBlockAccessListManager, IDisposable
{
    private BlockExecutionContext? _blockExecutionContext;
    private ITxProcessorWithWorldStateManager? _txProcessorWithWorldStateManager;
    private readonly Lazy<ParallelTxProcessorWithWorldStateManager> _parallelTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager, prewarmerEnvFactory, preBlockCaches, readOnlyTxProcessingEnvFactory));
    private readonly Lazy<SequentialTxProcessorWithWorldStateManager> _sequentialTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager));
    private const int GasValidationChunkSize = 8;
    private long? _gasRemaining;
    private bool _isBuilding;
    private bool _blockAccessListsEnabled;

    // Snapshot point for parallel workers' parent-reader scopes. Set only when
    // ParallelExecutionEnabled; null on the sequential path so a stray scope opens fail fast.
    private Hash256? _parentStateRoot;

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
    // Latched when a per-tx slice surfaces a generated-only account that the column index
    // can't see (no lane data on either side). Forces the validator's fallback walk so the
    // same "missing account changes" error fires as on the sequential path.
    private bool _hasGeneratedRequiredReadAccountMismatch;

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

    /// <summary>When set, the manager always builds the materialised GeneratedBlockAccessList
    /// even on the parallel-validation path where the column-index validator suffices on its own.
    /// Wrappers that read the materialised BAL after processing (BAL recorder, RPC diagnostics)
    /// must set this before PrepareForProcessing runs.</summary>
    public bool ForceMaterializeGeneratedBlockAccessList { get; set; }

    // Replaces the end-of-block encode + Keccak (and now the per-tx Merge) with a column-index-
    // only validation path. See paradigmxyz/reth#24297 for the prior art.
    private bool _verifyOnly;

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
    {
        _blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;
        Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;
        _isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock);

        // Parallel execution needs the decoded BAL body (RLP fixtures only carry the hash)
        // and an active state scope (so we can capture the parent state root for workers).
        ParallelExecutionEnabled = Enabled
            && blocksConfig.ParallelExecution
            && !_isBuilding
            && suggestedBlock.BlockAccessList is not null
            && stateProvider.IsInScope;

        if (Enabled)
        {
            Reset();
            // Build the column-oriented validation index once per block; per-tx ChangesEqual
            // then collapses to row-aligned span compares. Tally suggested chargeable storage
            // reads here so the per-tx surplus-reads gas check avoids re-walking the BAL.
            // Only the parallel path feeds the generated side (via RegisterGeneratedSlice in
            // MergeAndReturnBal); the sequential NextTransaction merges directly into
            // GeneratedBlockAccessList, so the fast path never fires there — skip the build.
            if (ParallelExecutionEnabled && suggestedBlock.BlockAccessList is not null)
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
            _gasRemaining = suggestedBlock.GasUsed;
            _parentStateRoot = ParallelExecutionEnabled ? stateProvider.StateRoot : null;
            _verifyOnly = ParallelExecutionEnabled && !ForceMaterializeGeneratedBlockAccessList;
        }
    }

    public void Setup(Block block)
    {
        if (Enabled)
        {
            _txProcessorWithWorldStateManager = ParallelExecutionEnabled ? _parallelTxProcessorWithWorldStateManager.Value : _sequentialTxProcessorWithWorldStateManager.Value;
            CheckInitialized();
            _txProcessorWithWorldStateManager.Setup(block, _blockExecutionContext.Value, _parentStateRoot);
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
            MergeAndReturnBal();
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

    public void Dispose()
    {
        if (_parallelTxProcessorWithWorldStateManager.IsValueCreated)
        {
            _parallelTxProcessorWithWorldStateManager.Value.Dispose();
        }
    }

    /// <summary>Detach the slice for <paramref name="balIndex"/>, fold it into
    /// <see cref="GeneratedBlockAccessList"/>, and feed it to <see cref="RegisterGeneratedSlice"/>
    /// so the column-index fast path and read-only-account mismatch flag stay in sync.</summary>
    /// <remarks>The <paramref name="balIndex"/> default exists for the sequential per-tx hook
    /// (<see cref="NextTransaction"/>), where the underlying pool ignores the index. Parallel
    /// callers (<c>IncrementalValidation</c>, <c>SetBlockAccessList</c>) always pass an explicit
    /// value to identify the per-tx slot to detach.</remarks>
    private void MergeAndReturnBal(uint balIndex = 0)
        => _txProcessorWithWorldStateManager!.MergeAndReturnBal(
            balIndex,
            _verifyOnly ? null : GeneratedBlockAccessList,
            RegisterGeneratedSlice);

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
        _parentStateRoot = null;
        GeneratedBlockAccessList.Reset();
        _suggestedValidationIndex = null;
        _generatedValidationIndex = null;
        _suggestedChargeableStorageReads = 0;
        _generatedChargeableStorageReads = 0;
        _hasGeneratedValidationIndexUpdates = false;
        _hasGeneratedRequiredReadAccountMismatch = false;
        _verifyOnly = false;
    }

    [DoesNotReturn]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");
}
