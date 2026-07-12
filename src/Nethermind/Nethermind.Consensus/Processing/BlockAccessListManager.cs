// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
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
///   * BlockAccessListManager.SystemContracts.cs       — beacon root, blockhash, withdrawals, requests
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
    CodeInfoRepositoryFactory codeInfoRepositoryFactory,
    PrewarmerEnvFactory? prewarmerEnvFactory = null,
    PreBlockCaches? preBlockCaches = null,
    IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory = null,
    ITransactionProcessorFactory? transactionProcessorFactory = null,
    IExecutionRequestsProcessorFactory? executionRequestsProcessorFactory = null)
    : IBlockAccessListManager, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockAccessListManager>();
    private BlockExecutionContext? _blockExecutionContext;
    private ITxProcessorWithWorldStateManager? _txProcessorWithWorldStateManager;
    private Task? _balWarmupTask;
    private readonly Lazy<ParallelTxProcessorWithWorldStateManager> _parallelTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager, prewarmerEnvFactory, preBlockCaches, readOnlyTxProcessingEnvFactory,
            transactionProcessorFactory ?? new TransactionProcessorFactory<EthereumGasPolicy>(), codeInfoRepositoryFactory));
    private readonly Lazy<SequentialTxProcessorWithWorldStateManager> _sequentialTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager,
            transactionProcessorFactory ?? new TransactionProcessorFactory<EthereumGasPolicy>(), codeInfoRepositoryFactory));
    private const int GasValidationChunkSize = 8;
    private ulong? _gasRemaining;
    private bool _isBuilding;
    private bool _validateBlockAccessList;
    private bool _blockAccessListsEnabled;

    // Parallel execution requires this pool (mirrors CreateParentReaderEnvPool); witness/stateless supply none.
    private readonly bool _hasParentReaderPool =
        (prewarmerEnvFactory is not null && preBlockCaches is not null)
        || readOnlyTxProcessingEnvFactory is not null;

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
    private ulong _suggestedChargeableStorageReads;
    private ulong _generatedChargeableStorageReads;
    private bool _hasGeneratedValidationIndexUpdates;
    // for tests
    internal bool HasGeneratedValidationIndexUpdates => _hasGeneratedValidationIndexUpdates;
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
    public bool BatchReadEnabled { get; private set; }

    /// <summary>
    /// When set, the manager always builds the constructed GeneratedBlockAccessList even on
    /// the parallel-validation path where the column-index validator suffices on its own.
    /// Wrappers that read the constructed BAL after processing (BAL recorder, RPC diagnostics)
    /// must set this before PrepareForProcessing runs.
    /// </summary>
    public bool ForceConstructGeneratedBlockAccessList { get; set; }

    // Non-null when the manager is constructing the per-block aggregate (always points at
    // GeneratedBlockAccessList itself in that case); null on the verify-only path where the
    // column-index validator stands in for the constructed list. Drives both the per-tx
    // Merge target and the end-of-block encode + Keccak step.
    private GeneratedBlockAccessList? _currentGeneratedBlockAccessList;
    private bool VerifyOnly => _currentGeneratedBlockAccessList is null;

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
    {
        _blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;
        Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;
        _isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock);

        ParallelExecutionEnabled = Enabled
            && blocksConfig.ParallelExecution
            && !options.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList)
            && !_isBuilding
            && suggestedBlock.BlockAccessList is not null
            && stateProvider.IsInScope
            && _hasParentReaderPool;

        // BAL-driven read warming: mirrors BlockCachePreWarmer.IsBalReadWarmingEnabled so
        // HintBal honours the same opt-in config as the prewarmer path.
        BatchReadEnabled = Enabled && blocksConfig.ParallelExecutionBatchRead;

        if (Enabled)
        {
            Reset();
            _validateBlockAccessList = !options.ContainsFlag(ProcessingOptions.NoValidation) && !_isBuilding;
            // Build the column-oriented validation index once per block; per-tx ChangesEqual
            // then collapses to row-aligned span compares. Tally suggested chargeable storage
            // reads here so the per-tx surplus-reads gas check avoids re-walking the BAL.
            // Skipped when building a block or in RLP fixtures (no suggested)
            if (!_isBuilding && suggestedBlock.BlockAccessList is not null)
            {
                BlockAccessListValidationIndex.AddressIndex addressIndex = new();
                ReadOnlyBlockAccessList suggested = suggestedBlock.BlockAccessList;
                _suggestedValidationIndex = BlockAccessListValidationIndex.Build(suggested, suggestedBlock.Transactions.Length, addressIndex);
                _generatedValidationIndex = new(suggestedBlock.Transactions.Length, addressIndex, _suggestedValidationIndex, suggested.TotalStorageReads, suggested.TotalStorageChangeEvents);
                ulong suggestedReads = 0;
                foreach (ReadOnlyAccountChanges ac in suggested.AccountChanges)
                {
                    if (!IsSystemContract(ac.Address)) suggestedReads += (ulong)ac.StorageReads.Length;
                }
                _suggestedChargeableStorageReads = suggestedReads;
            }
            _gasRemaining = suggestedBlock.GasUsed;
            _parentStateRoot = ParallelExecutionEnabled ? stateProvider.StateRoot : null;
            _currentGeneratedBlockAccessList = (ParallelExecutionEnabled && !ForceConstructGeneratedBlockAccessList) ? null : GeneratedBlockAccessList;
        }

        _balWarmupTask = StartBalReadWarmup(suggestedBlock);
    }

    // Only the parallel executor drains the hint; sequential execution contends with the warming reads.
    private Task? StartBalReadWarmup(Block suggestedBlock)
    {
        if (!BatchReadEnabled || !ParallelExecutionEnabled || suggestedBlock.BlockAccessList is null)
            return null;

        try
        {
            return stateProvider.HintBal(suggestedBlock.BlockAccessList);
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Debug($"BAL read warming hint failed to start: {ex}");
            return null;
        }
    }

    public void WaitForBalWarmup()
    {
        Task? task = _balWarmupTask;
        if (task is null) return;
        _balWarmupTask = null;

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected when warming was already cancelled, e.g. by an earlier write batch.
        }
        catch (Exception ex)
        {
            // Warming is best-effort: a faulted task only means fewer pre-block cache hits and
            // must never fail the block. Log so a slow block can be correlated with the failure.
            if (_logger.IsDebug) _logger.Debug($"BAL read warming faulted: {ex}");
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

    public void SpendGas(ulong gas)
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
        DisposableExtensions.DisposeAndNull(ref _suggestedValidationIndex);
        DisposableExtensions.DisposeAndNull(ref _generatedValidationIndex);
    }

    /// <summary>
    /// Detach the slice for <paramref name="balIndex"/>, fold it into
    /// <see cref="GeneratedBlockAccessList"/> (or skip the fold in verify-only mode), and feed
    /// it to <see cref="RegisterGeneratedSlice"/> so the column-index fast path stays in sync.
    /// </summary>
    /// <remarks>
    /// The <paramref name="balIndex"/> default exists for the sequential per-tx hook
    /// (<see cref="NextTransaction"/>), where the underlying pool ignores the index. Parallel
    /// callers (<c>IncrementalValidation</c>, <c>SetBlockAccessList</c>) always pass an explicit
    /// value to identify the per-tx slot to detach.
    /// </remarks>
    private void MergeAndReturnBal(uint balIndex = 0)
        => _txProcessorWithWorldStateManager!.MergeAndReturnBal(
            balIndex,
            _currentGeneratedBlockAccessList,
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
        _validateBlockAccessList = false;
        _parentStateRoot = null;
        GeneratedBlockAccessList.Reset();
        DisposableExtensions.DisposeAndNull(ref _suggestedValidationIndex);
        DisposableExtensions.DisposeAndNull(ref _generatedValidationIndex);
        _suggestedChargeableStorageReads = 0ul;
        _generatedChargeableStorageReads = 0ul;
        _hasGeneratedValidationIndexUpdates = false;
        _hasGeneratedRequiredReadAccountMismatch = false;
        _currentGeneratedBlockAccessList = null;
    }

    [DoesNotReturn]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");
}
