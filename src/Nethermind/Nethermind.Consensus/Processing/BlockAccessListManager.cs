// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Coordinates all BAL-aware processing for a block: spinning up the right tx-processor pool
/// (parallel vs sequential), merging the logs the processors recorded into the block's list,
/// and orchestrating tx execution → incremental gas validation → end-of-block list validation.
/// Implementation is split across partial files by concern:
///   * BlockAccessListManager.cs                       — lifecycle, per-tx hot path, fields
///   * BlockAccessListManager.Validation.cs            — incremental gas, per-tx 2D inclusion check, list validation
///   * BlockAccessListManager.StateChanges.cs          — SetBlockAccessList
///   * BlockAccessListManager.SystemContracts.cs       — beacon root, blockhash, withdrawals, requests
///   * BlockAccessListManager.TxProcessorPool.cs       — nested pool / processor / world-state types
/// </summary>
/// <remarks>
/// Parent-state fallbacks (for slots the suggested BAL doesn't cover) flow through a pooled
/// <see cref="IReadOnlyTxProcessingEnvFactory"/>: each parallel worker rents a snapshot at the
/// parent of the block being processed. Passing <c>null</c>
/// disables this and thus parallel execution.
/// </remarks>
public partial class BlockAccessListManager(
    IWorldState stateProvider,
    ILogManager logManager,
    IBlocksConfig blocksConfig,
    IWithdrawalProcessorFactory withdrawalProcessorFactory,
    BalTxProcessorFactory txProcessorFactory,
    PrewarmerEnvFactory? prewarmerEnvFactory = null,
    PreBlockCaches? preBlockCaches = null,
    IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory = null,
    ZeroNonceStorageAccountsTransition? zeroNonceStorageAccountsTransition = null)
    : IBlockAccessListManager, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockAccessListManager>();
    private BlockExecutionContext? _blockExecutionContext;
    private ITxProcessorWithWorldStateManager? _txProcessorWithWorldStateManager;
    private Task? _balWarmupTask;
    // Null in a build that folds parallel execution out, so nothing behind the pool is compiled.
    private readonly Lazy<ParallelTxProcessorWithWorldStateManager>? _parallelTxProcessorWithWorldStateManager = !ZkEvmFlag.IsActive
        ? new Lazy<ParallelTxProcessorWithWorldStateManager>(() => new(stateProvider, logManager, prewarmerEnvFactory, preBlockCaches, readOnlyTxProcessingEnvFactory, txProcessorFactory))
        : null;
    private readonly Lazy<SequentialTxProcessorWithWorldStateManager> _sequentialTxProcessorWithWorldStateManager =
        new(() => new(stateProvider, logManager, txProcessorFactory));
    private const int GasValidationChunkSize = 8;
    private ulong? _gasRemaining;
    private bool _isBuilding;
    private bool _blockAccessListsEnabled;

    // Parallel execution requires this pool (mirrors CreateParentReaderEnvPool); witness/stateless supply none.
    private readonly bool _hasParentReaderPool =
        (prewarmerEnvFactory is not null && preBlockCaches is not null)
        || readOnlyTxProcessingEnvFactory is not null;

    private readonly MergedBlockAccessList _mergedBlockAccessList = new();
    // Whether the merged list is checked against the suggested one rather than only encoded.
    private bool _validateBlockAccessList;
    // EIP-7928: post-execution system calls read storage without spending block gas, so the
    // surplus-reads budget must leave room for what their own execution grant can read.
    private ulong _postExecutionReadAllowance;

    public class ParallelExecutionException(InvalidBlockException innerException)
        : InvalidTransactionException(
            innerException.InvalidBlock,
            innerException.Message,
            (innerException as InvalidTransactionException)?.Reason
                ?? TransactionResult.ErrorType.MalformedTransaction.WithDetail($"Parallel execution failure: {innerException.Message}"),
            innerException);

    public GeneratedBlockAccessList GeneratedBlockAccessList { get; set; } = new();
    public BlockAccessListItemSet BlockAccessListItems { get; } = new();
    public bool Enabled { get; private set; }
    public bool ParallelExecutionEnabled { get; private set; }
    public bool BatchReadEnabled { get; private set; }

    /// <summary>
    /// When set, the manager always builds the constructed GeneratedBlockAccessList even on
    /// the parallel-validation path where the merged list suffices on its own.
    /// Wrappers that read the constructed BAL after processing (BAL recorder, RPC diagnostics)
    /// must set this before PrepareForProcessing runs.
    /// </summary>
    public bool ForceConstructGeneratedBlockAccessList { get; set; }

    // Set on the parallel-validation path, where the merged list is only compared with the suggested one.
    private bool VerifyOnly { get; set; }

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
    {
        _blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;
        Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;
        _isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock);

        ParallelExecutionEnabled = !ZkEvmFlag.IsActive
            && Enabled
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
            VerifyOnly = ParallelExecutionEnabled && !ForceConstructGeneratedBlockAccessList;
            // The parallel path executes on the suggested list, so it always checks it.
            _validateBlockAccessList = suggestedBlock.BlockAccessList is not null
                && (ParallelExecutionEnabled
                    || (!options.ContainsFlag(ProcessingOptions.NoValidation)
                        && !_isBuilding
                        && !options.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList)));
            _gasRemaining = suggestedBlock.GasUsed;
        }

        _balWarmupTask = StartBalReadWarmup(suggestedBlock);
    }

    // Only the parallel executor drains the hint; sequential execution contends with the warming reads.
    private Task? StartBalReadWarmup(Block suggestedBlock)
    {
        if (ZkEvmFlag.IsActive || !BatchReadEnabled || !ParallelExecutionEnabled || suggestedBlock.BlockAccessList is null)
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
        // Only the parallel path starts warming, so a build without it has nothing to wait for.
        if (ZkEvmFlag.IsActive) return;

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
        // A block that does not run in parallel releases what an earlier parallel block that failed still kept.
        if (!ParallelExecutionEnabled && _parallelTxProcessorWithWorldStateManager is { IsValueCreated: true } parallel)
        {
            parallel.Value.ReleaseKeptParentReaders();
        }

        if (Enabled)
        {
            _txProcessorWithWorldStateManager = !ZkEvmFlag.IsActive && ParallelExecutionEnabled
                ? _parallelTxProcessorWithWorldStateManager!.Value
                : _sequentialTxProcessorWithWorldStateManager.Value;
            CheckInitialized();
            _txProcessorWithWorldStateManager.Setup(block, _blockExecutionContext.Value);
            // Read from the state before the block, which the post-execution calls' code can differ from only when the block deploys it.
            _postExecutionReadAllowance = _validateBlockAccessList && ChargeableStorageReads(block.BlockAccessList!) > 0ul
                ? PostExecutionReadAllowance(_blockExecutionContext.Value.Spec)
                : 0ul;
        }
    }

    public void SpendGas(ulong gas)
    {
        CheckInitialized();
        _gasRemaining = _gasRemaining.Value.SaturatingSub(gas);
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
            if (_isBuilding)
            {
                BlockAccessIndexRecorder recorder = GetBuildingRecorder();
                BlockAccessListItems.Add(recorder.Log, recorder.Start);
            }
            _txProcessorWithWorldStateManager.NextTransaction();
        }
    }

    public int CountPendingTransactionItems()
    {
        if (!Enabled || !_isBuilding) return 0;

        CheckInitialized();
        BlockAccessIndexRecorder recorder = GetBuildingRecorder();
        return BlockAccessListItems.CountNewItems(recorder.Log, recorder.Start);
    }

    // Block building runs sequentially, so the current index is the single processor's.
    private BlockAccessIndexRecorder GetBuildingRecorder()
        => _txProcessorWithWorldStateManager!.Get(null).WorldState.GetGeneratingBlockAccessList()!;

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
        if (!ZkEvmFlag.IsActive && Enabled && ParallelExecutionEnabled)
        {
            // Recycle the pool slot; the worker's log keeps what it recorded until the block's list is merged.
            _parallelTxProcessorWithWorldStateManager!.Value.Return(balIndex);
        }
    }

    public void Dispose()
    {
        if (!ZkEvmFlag.IsActive && _parallelTxProcessorWithWorldStateManager!.IsValueCreated)
        {
            _parallelTxProcessorWithWorldStateManager.Value.Dispose();
        }
        if (_sequentialTxProcessorWithWorldStateManager.IsValueCreated)
        {
            _sequentialTxProcessorWithWorldStateManager.Value.Dispose();
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
        BlockAccessListItems.Clear();
        _mergedBlockAccessList.Clear();
    }

    [DoesNotReturn]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");
}
