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
/// and orchestrating tx execution → incremental validation → state apply.
/// Implementation is split across partial files by concern:
///   * BlockAccessListManager.cs                       — lifecycle, per-tx hot path, fields
///   * BlockAccessListManager.Validation.cs            — incremental + per-tx 2D inclusion check
///   * BlockAccessListManager.StateChanges.cs          — ApplyStateChanges, SetBlockAccessList
///   * BlockAccessListManager.SystemContracts.cs       — beacon root, blockhash, AuRa, withdrawals, requests
///   * BlockAccessListManager.TxProcessorPool.cs       — nested pool / processor / world-state types
/// </summary>
/// <remarks>
/// Parent-state reads (for values the suggested BAL doesn't carry at the current index) flow
/// through a pooled <see cref="IReadOnlyTxProcessingEnvFactory"/>. Each parallel worker rents
/// its own snapshot scope of the parent state, scoped against the state root captured at
/// <see cref="PrepareForProcessing"/>. The factory is supplied via DI; passing <c>null</c>
/// disables parent-reader-based parallel execution.
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

    // State root captured at PrepareForProcessing — the snapshot point against which each
    // parallel worker's parent-reader scope is opened. Captured only when ParallelExecutionEnabled
    // (which itself requires stateProvider.IsInScope). Null on the sequential path so attempts
    // to build a parent-reader scope without an active state scope fail fast.
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
    // Latched true if any tx-level slice introduces an account that has no state changes
    // and isn't a tolerated read-only entry (system-user at index 0 or any storage-read row),
    // and that account isn't declared in the suggested BAL. The column-index fast path can't
    // detect this — no lane data lands for such an account on either side, so ChangesEqual
    // says "equal". The flag forces ValidateBlockAccessList's fallback walk, which produces
    // the same "missing account changes" error the sequential path surfaces.
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

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
    {
        _blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;
        Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;
        _isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock);

        // Parallel execution requires:
        //  * the BAL body to be present on the block (RLP-only fixtures only carry the hash);
        //  * the state provider to be scoped, so we can capture the parent state root for the
        //    per-worker snapshot reader.
        ParallelExecutionEnabled = Enabled
            && blocksConfig.ParallelExecution
            && !_isBuilding
            && suggestedBlock.BlockAccessList is not null
            && stateProvider.IsInScope;

        if (Enabled)
        {
            Reset();
            _gasRemaining = suggestedBlock.GasUsed;
            _parentStateRoot = ParallelExecutionEnabled ? stateProvider.StateRoot : null;

            // Build the column-oriented validation index used by ValidateBlockAccessList's
            // fast path. Runs once per block; subsequent per-tx ChangesEqual calls become
            // row-aligned span compares. Tally suggested-side chargeable storage reads here
            // so the per-tx surplus-reads gas check can avoid re-walking the BAL.
            //
            // Only the parallel path feeds the generated index (via RegisterGeneratedSlice
            // wired into MergeAndReturnBal). The sequential path's NextTransaction merges
            // BALs directly through WorldState.MergeGeneratingBal without going through the
            // callback, so _hasGeneratedValidationIndexUpdates would never flip and the
            // fast path would never trigger — skip the O(n) build entirely there.
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
    private void MergeAndReturnBal(uint balIndex)
        => _txProcessorWithWorldStateManager!.MergeAndReturnBal(balIndex, GeneratedBlockAccessList, RegisterGeneratedSlice);

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
    }

    [DoesNotReturn]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");
}
