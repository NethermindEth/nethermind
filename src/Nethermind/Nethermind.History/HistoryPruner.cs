// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

[assembly: InternalsVisibleTo("Nethermind.History.Test")]

namespace Nethermind.History;

public class HistoryPruner : IHistoryPruner
{
    private const int LockWaitTimeoutMs = 100;
    private const ulong SlotsPerEpoch = 32;

    public ulong GetRetentionBlocks(ulong retentionEpochs) => retentionEpochs * SlotsPerEpoch;

    private readonly object _pruneLock = new();

    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IBlockAccessListStore _blockAccessListStore;
    private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
    private readonly IDb _metadataDb;
    private readonly IProcessExitSource _processExitSource;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly IHistoryConfig _historyConfig;
    private readonly bool _enabled;
    private readonly ulong _pruningInterval;
    private readonly ulong _minHistoryRetentionEpochs;
    private readonly ulong _minBalRetentionEpochs;
    private readonly ulong _ancientBarrier;
    private readonly ulong _minDeletableBlockNumber;

    private ulong _blocksDeletePointer = 1;
    private ulong _balsDeletePointer = 1;
    private ulong _lastSavedBlocksDeletePointer = 1;
    private ulong _lastSavedBalsDeletePointer = 1;
    private BlockHeader? _oldestBlockHeader;
    private bool _hasLoadedDeletePointers;
    private int _currentlyPruning;

    public event EventHandler<OnNewOldestBlockArgs>? NewOldestBlock;

    public class HistoryPrunerException(string message, Exception? innerException = null) : Exception(message, innerException);

    public HistoryPruner(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        IBlockAccessListStore blockAccessListStore,
        ISpecProvider specProvider,
        IChainLevelInfoRepository chainLevelInfoRepository,
        IDbProvider dbProvider,
        IHistoryConfig historyConfig,
        IBlocksConfig blocksConfig,
        ISyncConfig syncConfig,
        IProcessExitSource processExitSource,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        IBlockProcessingQueue blockProcessingQueue,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<HistoryPruner>();
        _deletionProgressLoggingInterval = _logger.IsDebug ? 5 : 100000;
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        _blockAccessListStore = blockAccessListStore;
        _chainLevelInfoRepository = chainLevelInfoRepository;
        _metadataDb = dbProvider.MetadataDb;
        _processExitSource = processExitSource;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _historyConfig = historyConfig;
        _enabled = historyConfig.Enabled();
        _pruningInterval = historyConfig.PruningInterval * SlotsPerEpoch;
        _minHistoryRetentionEpochs = specProvider.GenesisSpec.MinHistoryRetentionEpochs;
        _minBalRetentionEpochs = specProvider.GenesisSpec.MinBalRetentionEpochs;
        _minDeletableBlockNumber = (_blockTree.Genesis?.Number ?? 0) + 1; // do not remove genesis

        CheckConfig();

        if (_enabled)
        {
            if (historyConfig.Pruning == PruningModes.UseAncientBarriers)
            {
                _ancientBarrier = ulong.Min(syncConfig.AncientBodiesBarrierCalc, syncConfig.AncientReceiptsBarrierCalc);
            }
            Metrics.PruningCutoffBlocknumber = CutoffBlockNumber;
            Metrics.BlockAccessListPruningCutoffBlocknumber = BalCutoffBlockNumber;

            blockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
        }
    }

    public ulong? CutoffBlockNumber
    {
        get
        {
            if (!_enabled)
            {
                return null;
            }

            return _historyConfig.Pruning == PruningModes.UseAncientBarriers
                ? _ancientBarrier
                : CalculateRollingCutoff(_historyConfig.RetentionEpochs);
        }
    }

    public ulong? BalCutoffBlockNumber => _enabled ? CalculateRollingCutoff(_historyConfig.BalRetentionEpochs) : null;

    internal ulong BalsDeletePointer => _balsDeletePointer;

    public BlockHeader? OldestBlockHeader
    {
        get
        {
            if (!_hasLoadedDeletePointers)
            {
                bool lockTaken = false;
                // take lock before updating delete pointer
                // avoids race conditions with pruning
                try
                {
                    Monitor.TryEnter(_pruneLock, LockWaitTimeoutMs, ref lockTaken);
                    if (lockTaken)
                    {
                        if (!TryLoadDeletePointers())
                        {
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
                finally
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(_pruneLock);
                    }
                }
            }

            return _oldestBlockHeader;
        }
    }

    private ulong? CalculateRollingCutoff(uint retentionEpochs)
    {
        ulong? head = _blockTree.Head?.Number;
        if (head is null)
        {
            return null;
        }

        ulong blocksToRetain = retentionEpochs * SlotsPerEpoch;
        return head.Value.SaturatingSub(blocksToRetain);
    }

    private void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
        => SchedulePruneHistory(_processExitSource.Token);

    /// <summary>
    /// Schedules a pruning operation if one is not already running. Pruning will only be performed if the configured pruning interval has elapsed and there are blocks eligible for pruning.
    /// Cancelled when timeout elapses or process is exiting, to avoid long pruning operations during shutdown. Will be rescheduled on next trigger if pruning could not be completed.
    /// </summary>
    public void SchedulePruneHistory() => SchedulePruneHistory(_processExitSource.Token);

    protected void SchedulePruneHistory(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _currentlyPruning) == 0)
        {
            Task.Run(() =>
            {
                if (Interlocked.CompareExchange(ref _currentlyPruning, 1, 0) == 0)
                {
                    try
                    {
                        TimeSpan? pruningTimeout = _historyConfig.PruningTimeoutSeconds > 0
                            ? TimeSpan.FromSeconds(_historyConfig.PruningTimeoutSeconds)
                            : null;
                        if (!_backgroundTaskScheduler.TryScheduleTask(1,
                                (_, backgroundTaskToken) =>
                                {
                                    try
                                    {
                                        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(backgroundTaskToken,
                                            cancellationToken);
                                        TryPruneHistory(cts.Token);
                                    }
                                    finally
                                    {
                                        Interlocked.Exchange(ref _currentlyPruning, 0);
                                    }

                                    return Task.CompletedTask;
                                }, timeout: pruningTimeout, source: "HistoryPruner"))
                        {
                            Interlocked.Exchange(ref _currentlyPruning, 0);
                            if (_logger.IsDebug) _logger.Debug("Failed to schedule historical block pruning (queue full). Will retry on next trigger.");
                        }
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _currentlyPruning, 0);
                        throw;
                    }
                }
            });
        }
    }

    internal void TryPruneHistory(CancellationToken cancellationToken)
    {
        if (_blockTree.Head is null ||
            _blockTree.SyncPivot.BlockNumber == 0 ||
            !ShouldPruneHistory())
        {
            SkipLocalPruning();
            return;
        }

        bool lockTaken = false;
        Monitor.TryEnter(_pruneLock, LockWaitTimeoutMs, ref lockTaken);
        try
        {
            if (lockTaken)
            {
                if (!TryLoadDeletePointers() || !ShouldPruneHistory())
                {
                    SkipLocalPruning();
                    return;
                }

                ulong? blockCutoff = CutoffBlockNumber;
                ulong? balCutoff = BalCutoffBlockNumber;
                Metrics.PruningCutoffBlocknumber = blockCutoff;
                Metrics.BlockAccessListPruningCutoffBlocknumber = balCutoff;

                ulong syncPivot = _blockTree.SyncPivot.BlockNumber;
                ulong blockUpper = blockCutoff is null ? _blocksDeletePointer : ulong.Min(blockCutoff.Value, syncPivot);
                ulong balUpper = balCutoff is null ? _balsDeletePointer : ulong.Min(balCutoff.Value, syncPivot);

                if (_logger.IsInfo)
                {
                    ulong blocksRemaining = blockUpper.SaturatingSub(_blocksDeletePointer);
                    ulong balsRemaining = balUpper.SaturatingSub(_balsDeletePointer);
                    _logger.Info($"Pruning historical blocks up to #{blockUpper} ({blocksRemaining} estimated) and block access lists up to #{balUpper} ({balsRemaining} estimated).");
                }

                PruneBlocksAndReceipts(blockUpper, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    PruneBlockAccessLists(balUpper, cancellationToken);
                }
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug("Skipping historical pruning, task already running.");
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_pruneLock);
            }
        }

        void SkipLocalPruning()
        {
            if (_logger.IsTrace) _logger.Trace("Skipping historical block pruning.");
        }
    }

    internal bool SetDeletePointerToOldestBlock()
    {
        ulong? oldestBlockNumber = BlockTree.BinarySearchBlockNumber(
            _minDeletableBlockNumber,
            _blockTree.SyncPivot.BlockNumber,
            BlockExists,
            BlockTree.BinarySearchDirection.Down);

        if (oldestBlockNumber is not null)
        {
            UpdateBlocksDeletePointer(oldestBlockNumber.Value);
            SaveDeletePointers();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Callback for <see cref="BlockTree.BinarySearchBlockNumber"/>. Must match
    /// <c>Func&lt;ulong, bool, bool&gt;</c>.
    /// </summary>
    private bool BlockExists(ulong n, bool _)
    {
        ChainLevelInfo? info = _chainLevelInfoRepository.LoadLevel(n);

        if (info is null)
        {
            return false;
        }

        foreach (BlockInfo blockInfo in info.BlockInfos)
        {
            Block? b = _blockTree.FindBlock(blockInfo.BlockHash, n);
            if (b is not null)
            {
                return true;
            }
        }

        return false;
    }

    private void CheckConfig()
    {
        if (_historyConfig.RetentionEpochs < _minHistoryRetentionEpochs)
        {
            throw new HistoryPrunerException($"HistoryRetentionEpochs must be at least {_minHistoryRetentionEpochs}.");
        }
        if (_historyConfig.BalRetentionEpochs < _minBalRetentionEpochs)
        {
            throw new HistoryPrunerException($"BalRetentionEpochs must be at least {_minBalRetentionEpochs}.");
        }
    }

    private bool ShouldPruneHistory()
    {
        if (!_enabled || !PruningIntervalHasElapsed())
        {
            return false;
        }

        ulong? blockCutoff = CutoffBlockNumber;
        ulong? balCutoff = BalCutoffBlockNumber;
        return (blockCutoff is { } bc && _blocksDeletePointer < bc)
            || (balCutoff is { } balC && _balsDeletePointer < balC);
    }

    private bool PruningIntervalHasElapsed()
        => _pruningInterval == 0 || _blockTree.Head!.Number % _pruningInterval == 0;

    private readonly int _deletionProgressLoggingInterval;

    private void PruneBlocksAndReceipts(ulong upperExclusive, CancellationToken cancellationToken)
    {
        int deletedBlocks = 0;
        try
        {
            for (ulong number = _blocksDeletePointer; number < upperExclusive; number++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Block pruning operation timed out at #{number}. Deleted {deletedBlocks} blocks.");
                    break;
                }

                // Defensive guards: never delete genesis or blocks at/past the sync pivot.
                if (number < _minDeletableBlockNumber || number >= _blockTree.SyncPivot.BlockNumber)
                {
                    if (_logger.IsWarn) _logger.Warn($"Encountered unexpected block #{number} while pruning history, this block will not be deleted. Should be in range [{_minDeletableBlockNumber}, {_blockTree.SyncPivot.BlockNumber}).");
                    continue;
                }

                ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(number);
                if (chainLevelInfo is not null)
                {
                    foreach (BlockInfo blockInfo in chainLevelInfo.BlockInfos)
                    {
                        Block? block = _blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None, number);
                        if (block is null)
                        {
                            continue;
                        }

                        if (_logger.IsDebug) _logger.Debug($"Deleting old block {number} with hash {blockInfo.BlockHash}.");
                        _blockTree.DeleteOldBlock(number, blockInfo.BlockHash);
                        _receiptStorage.RemoveReceipts(block);
                        // Only delete the BAL if the BAL-only pass hasn't already covered this block;
                        // otherwise the delete is a no-op and the counter would over-report.
                        if (number >= _balsDeletePointer)
                        {
                            _blockAccessListStore.Delete(number, blockInfo.BlockHash);
                            Metrics.BlockAccessListsPruned++;
                        }
                        deletedBlocks++;
                        Metrics.BlocksPruned++;
                    }
                }

                if (_logger.IsInfo && deletedBlocks > 0 && deletedBlocks % _deletionProgressLoggingInterval == 0)
                {
                    ulong remaining = upperExclusive.SaturatingSub(number + 1);
                    _logger.Info($"Historical block pruning in progress... Deleted {deletedBlocks} blocks, with {remaining} remaining.");
                }

                UpdateBlocksDeletePointer(number + 1, isFinalUpdate: number + 1 >= upperExclusive);
                if (_balsDeletePointer < _blocksDeletePointer)
                {
                    _balsDeletePointer = _blocksDeletePointer;
                    Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;
                }
            }
        }
        finally
        {
            SaveDeletePointers();

            if (!cancellationToken.IsCancellationRequested && _logger.IsInfo && deletedBlocks > 0)
            {
                _logger.Info($"Completed block pruning operation up to #{_blocksDeletePointer}. Deleted {deletedBlocks} blocks.");
            }
        }
    }

    private void PruneBlockAccessLists(ulong upperExclusive, CancellationToken cancellationToken)
    {
        // BAL-only pruning for the range past the block cutoff. Blocks (with their BALs) up to
        // _blocksDeletePointer have already been pruned by PruneBlocksAndReceipts.
        int deletedBals = 0;
        try
        {
            for (ulong number = _balsDeletePointer; number < upperExclusive; number++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Block access list pruning operation timed out at #{number}. Deleted {deletedBals} BALs.");
                    break;
                }

                if (number < _minDeletableBlockNumber || number >= _blockTree.SyncPivot.BlockNumber)
                {
                    continue;
                }

                ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(number);
                if (chainLevelInfo is not null)
                {
                    foreach (BlockInfo blockInfo in chainLevelInfo.BlockInfos)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Deleting old block access list at #{number} with hash {blockInfo.BlockHash}.");
                        _blockAccessListStore.Delete(number, blockInfo.BlockHash);
                        deletedBals++;
                        Metrics.BlockAccessListsPruned++;
                    }
                }

                _balsDeletePointer = number + 1;
                Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;

                if (_logger.IsInfo && deletedBals > 0 && deletedBals % _deletionProgressLoggingInterval == 0)
                {
                    ulong remaining = upperExclusive.SaturatingSub(number + 1);
                    _logger.Info($"Historical block access list pruning in progress... Deleted {deletedBals} BALs, with {remaining} remaining.");
                }
            }
        }
        finally
        {
            SaveDeletePointers();

            if (!cancellationToken.IsCancellationRequested && _logger.IsInfo && deletedBals > 0)
            {
                _logger.Info($"Completed block access list pruning operation up to #{_balsDeletePointer}. Deleted {deletedBals} BALs.");
            }
        }
    }

    private bool TryLoadDeletePointers()
    {
        if (_hasLoadedDeletePointers)
        {
            return true;
        }

        byte[]? blocksVal = _metadataDb.Get(MetadataDbKeys.HistoryPruningDeletePointer);
        if (blocksVal is null)
        {
            if (!SetDeletePointerToOldestBlock())
            {
                return false;
            }
        }
        else
        {
            UpdateBlocksDeletePointer(ulong.Max(new RlpReader(blocksVal).DecodeULong(), _minDeletableBlockNumber));
            _lastSavedBlocksDeletePointer = _blocksDeletePointer;
        }

        byte[]? balsVal = _metadataDb.Get(MetadataDbKeys.BlockAccessListPruningDeletePointer);
        // Until BAL pruning runs once, the BAL pointer trails the blocks pointer because BALs are
        // deleted alongside blocks in PruneBlocksAndReceipts. Default to the blocks pointer on first load.
        _balsDeletePointer = balsVal is null
            ? _blocksDeletePointer
            : ulong.Max(new RlpReader(balsVal).DecodeULong(), _blocksDeletePointer);
        // ulong.MaxValue is used as sentinel: guarantees SaveDeletePointers saves on the very first call.
        _lastSavedBalsDeletePointer = balsVal is null ? ulong.MaxValue : _balsDeletePointer;
        Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;

        _hasLoadedDeletePointers = true;
        if (_logger.IsDebug) _logger.Debug($"Discovered oldest block stored #{_blocksDeletePointer}, oldest BAL stored #{_balsDeletePointer}.");
        return true;
    }

    private void SaveDeletePointers()
    {
        if (!_hasLoadedDeletePointers)
        {
            return;
        }

        if (_blocksDeletePointer != _lastSavedBlocksDeletePointer)
        {
            _metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(_blocksDeletePointer).Bytes);
            _lastSavedBlocksDeletePointer = _blocksDeletePointer;
            if (_logger.IsDebug) _logger.Debug($"Persisting oldest block stored = #{_blocksDeletePointer} to disk.");
        }

        if (_balsDeletePointer != _lastSavedBalsDeletePointer)
        {
            _metadataDb.Set(MetadataDbKeys.BlockAccessListPruningDeletePointer, Rlp.Encode(_balsDeletePointer).Bytes);
            _lastSavedBalsDeletePointer = _balsDeletePointer;
            if (_logger.IsDebug) _logger.Debug($"Persisting oldest BAL stored = #{_balsDeletePointer} to disk.");
        }
    }

    private void UpdateBlocksDeletePointer(ulong newDeletePointer, bool isFinalUpdate = true)
    {
        _blocksDeletePointer = newDeletePointer;
        Metrics.OldestStoredBlockNumber = _blocksDeletePointer;
        _blockTree.NewOldestBlock(_blocksDeletePointer);
        BlockHeader? oldest = _blockTree.FindBlock(_blocksDeletePointer)?.Header;
        if (oldest is not null)
        {
            _oldestBlockHeader = oldest;
            NewOldestBlock?.Invoke(this, new OnNewOldestBlockArgs(oldest, isFinalUpdate));
        }
    }
}
