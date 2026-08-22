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
    // How far the disk has actually been given back, which trails the published boundary above and is nobody's
    // business but this class's. Split from the pointer on purpose: the boundary is a promise, this is bookkeeping.
    private ulong _blocksReclaimCursor = 1;
    private ulong _balsDeletePointer = 1;
    private ulong _lastSavedBlocksDeletePointer = 1;
    private ulong _lastSavedBlocksReclaimCursor = 1;
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

                // Unconditional: gating this on the blocks pass finishing meant that whenever that pass was cut
                // short - which was every pass - access lists were never pruned at all. It checks the token itself
                // and advances its own pointer per chunk, so entering it with a spent token costs one no-op.
                PruneBlockAccessLists(balUpper, cancellationToken);
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
            // Reclaim owed for a boundary already published. Without this the pointer jumping straight to the
            // cutoff would make the next pass see nothing to do and abandon the disk behind it.
            || _blocksReclaimCursor < _blocksDeletePointer
            || (balCutoff is { } balC && _balsDeletePointer < balC);
    }

    private bool PruningIntervalHasElapsed()
        => _pruningInterval == 0 || _blockTree.Head!.Number % _pruningInterval == 0;

    /// <summary>
    /// Blocks reclaimed per range removal. The removal itself costs the same whatever it spans, so this is not a
    /// throughput knob - it bounds how much one irreversible step can affect, keeps progress visible in the log, and
    /// gives cancellation somewhere to land.
    /// </summary>
    private const ulong ReclaimChunkBlocks = 1_000_000;

    /// <summary>
    /// Publishes the boundary for the whole span first, then gives the disk back behind it. That order is what makes
    /// the pass safe to interrupt: the announced boundary is a policy decision costing one metadata write, so it can
    /// never be starved, and everything the reclaim touches has already been declared absent. A reclaim that is slow,
    /// cancelled or lost to a crash leaves the node honest and merely fat - it resumes from the persisted cursor.
    /// </summary>
    private void PruneBlocksAndReceipts(ulong upperExclusive, CancellationToken cancellationToken)
    {
        // Never at or past the sync pivot: it is the floor re-execution can start from.
        ulong target = ulong.Min(upperExclusive, _blockTree.SyncPivot.BlockNumber);

        // Publish first, and raise-only - a lower cutoff must never walk the boundary back onto data already gone.
        // From here the node offers nothing below it, so the reclaim can only ever touch blocks already declared
        // absent, which is what makes an interrupted reclaim harmless.
        if (target > _blocksDeletePointer)
        {
            UpdateBlocksDeletePointer(target, isFinalUpdate: true);
            SaveDeletePointers();
        }

        // Never genesis. The reclaim chases the published boundary, NOT the cutoff: they part company the moment a
        // pass is interrupted, and the cursor is the only thing that knows where the disk actually stands.
        // Re-clamped against the live pivot rather than trusting the one that was current when the boundary was
        // published, possibly in an earlier process: the boundary is durable, the pivot is not monotonic in config.
        ulong limit = ulong.Min(_blocksDeletePointer, _blockTree.SyncPivot.BlockNumber);
        ulong start = ulong.Max(_blocksReclaimCursor, _minDeletableBlockNumber);
        if (start >= limit) return;

        ulong reclaimed = 0;
        try
        {
            for (ulong from = start; from < limit;)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info(
                        $"Historical block reclaim interrupted at #{from}; the boundary is already published at #{limit} and the next pass resumes from here. Reclaimed {reclaimed} blocks.");
                    return;
                }

                ulong to = ulong.Min(from + ReclaimChunkBlocks, limit);
                _blockTree.DeleteOldBlockRange(from, to);
                _receiptStorage.RemoveReceiptsRange(from, to);
                _blockAccessListStore.DeleteRange(from, to);

                // After the removals, so a crash between the two only costs repeating a chunk - and repeating one is
                // free, a range removal being idempotent.
                _blocksReclaimCursor = to;
                // Kept level per chunk rather than after the loop: a cancelling return used to skip it, leaving the
                // access list pass to re-issue tombstones over a range it had already reclaimed.
                if (_balsDeletePointer < to)
                {
                    // Only the ground not already claimed, so the two passes cannot count the same heights twice
                    // and neither can end up reporting nothing for work it did.
                    Metrics.BlockAccessListsPruned += (long)(to - ulong.Max(from, _balsDeletePointer));
                    _balsDeletePointer = to;
                    Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;
                }

                SaveDeletePointers();

                reclaimed += to - from;
                Metrics.BlocksPruned += (long)(to - from);
                if (_logger.IsInfo) _logger.Info($"Reclaimed historical blocks #{from} to #{to - 1}, {limit.SaturatingSub(to)} remaining.");
                from = to;
            }

        }
        finally
        {
            SaveDeletePointers();

            if (!cancellationToken.IsCancellationRequested && _logger.IsInfo && reclaimed > 0)
            {
                _logger.Info($"Completed block pruning up to #{_blocksDeletePointer}. Reclaimed {reclaimed} heights.");
            }
        }
    }

    /// <summary>Access lists past the block cutoff, whose blocks are still retained. Unlike
    /// <see cref="PruneBlocksAndReceipts"/> there is no boundary to publish first - this pointer is local
    /// bookkeeping, announced to nobody - so the pointer simply follows the reclaim, chunk by chunk.</summary>
    private void PruneBlockAccessLists(ulong upperExclusive, CancellationToken cancellationToken)
    {
        ulong limit = ulong.Min(upperExclusive, _blockTree.SyncPivot.BlockNumber);
        ulong start = ulong.Max(_balsDeletePointer, _minDeletableBlockNumber);
        if (start >= limit) return;

        ulong reclaimed = 0;
        try
        {
            for (ulong from = start; from < limit;)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Block access list reclaim interrupted at #{from}. Reclaimed {reclaimed} access lists.");
                    return;
                }

                ulong to = ulong.Min(from + ReclaimChunkBlocks, limit);
                _blockAccessListStore.DeleteRange(from, to);

                _balsDeletePointer = to;
                Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;
                SaveDeletePointers();

                reclaimed += to - from;
                Metrics.BlockAccessListsPruned += (long)(to - from);
                from = to;
            }
        }
        finally
        {
            SaveDeletePointers();

            if (!cancellationToken.IsCancellationRequested && _logger.IsInfo && reclaimed > 0)
            {
                _logger.Info($"Completed block access list pruning up to #{_balsDeletePointer}. Reclaimed {reclaimed} access lists.");
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

        byte[]? reclaimVal = _metadataDb.Get(MetadataDbKeys.HistoryPruningReclaimCursor);
        // Absent on a database pruned by the per-block code: everything below its published boundary is already gone,
        // so the cursor starts level with it rather than re-walking history that has no blocks left in it.
        _blocksReclaimCursor = reclaimVal is null
            ? _blocksDeletePointer
            : ulong.Max(new RlpReader(reclaimVal).DecodeULong(), _minDeletableBlockNumber);
        _lastSavedBlocksReclaimCursor = reclaimVal is null ? ulong.MaxValue : _blocksReclaimCursor;

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

        if (_blocksReclaimCursor != _lastSavedBlocksReclaimCursor)
        {
            _metadataDb.Set(MetadataDbKeys.HistoryPruningReclaimCursor, Rlp.Encode(_blocksReclaimCursor).Bytes);
            _lastSavedBlocksReclaimCursor = _blocksReclaimCursor;
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
        // Headers are never pruned, so this is both the reliable source and the cheap one. Reading the body would
        // make the announcement conditional on data the reclaim is about to erase - and the boundary now moves in
        // one jump, so a single miss would leave peers uninformed instead of self-healing next iteration.
        BlockHeader? oldest = _blockTree.FindHeader(_blocksDeletePointer, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        if (oldest is not null)
        {
            _oldestBlockHeader = oldest;
            NewOldestBlock?.Invoke(this, new OnNewOldestBlockArgs(oldest, isFinalUpdate));
        }
    }
}
