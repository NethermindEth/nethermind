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
    private ulong _blocksReclaimCursor = 1;
    private byte[]? _txIndexSweepCursor;
    private bool _txIndexSweepCursorLoaded;
    private ulong _balsDeletePointer = 1;
    private ulong _lastSavedBlocksDeletePointer = 1;
    private ulong _lastSavedBlocksReclaimCursor = 1;
    private ulong _lastSavedBalsDeletePointer = 1;
    // Read by JSON-RPC and the sync server while the pruner writes them under a lock it can hold for a whole reclaim.
    private volatile BlockHeader? _oldestBlockHeader;
    private volatile bool _hasLoadedDeletePointers;
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
                        TryLoadDeletePointers();
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
        // Trustworthy only once loaded: on in-memory defaults a collapsed cutoff would hide a persisted backlog.
        if (_blockTree.Head is null ||
            _blockTree.SyncPivot.BlockNumber == 0 ||
            (_hasLoadedDeletePointers && !ShouldPruneHistory()))
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
                PruneBlockAccessLists(balUpper, cancellationToken);

                // Last, and the order matters: this is the only pass whose cost is not bounded by its range, so
                // ahead of the others it would take the whole timeout on every pass and starve them. The access
                // list pass is range deletes with nothing to read, so it costs this one almost nothing.
                SweepTransactionIndex(cancellationToken);
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
            // Reclaim owed behind a boundary already published, which the cutoff comparison above cannot see.
            || _blocksReclaimCursor < _blocksDeletePointer
            || (balCutoff is { } balC && _balsDeletePointer < balC);
    }

    private bool PruningIntervalHasElapsed()
        => _pruningInterval == 0 || _blockTree.Head!.Number % _pruningInterval == 0;

    private const ulong ReclaimChunkBlocks = 1_000_000;

    /// <summary>Step taken when the budget was already spent on arrival. Draining a backlog this way is slow, which
    /// is the right answer: a node in that state has no room to prune, and the guarantee wanted is that progress
    /// cannot reach zero, not that it is fast.</summary>
    private const ulong MinimumReclaimChunkBlocks = 100_000;

    /// <summary>
    /// Ceiling on entries examined per pass, not the thing that governs the rate: with a non-zero
    /// <c>PruningTimeoutSeconds</c> the token almost always ends the walk first. The index therefore settles at some
    /// stale fraction of the live set rather than at zero, and this bounds one pass rather than closing that gap.
    /// </summary>
    private const int TxIndexSweepEntriesPerPass = 500_000;

    /// <summary>Publishes the boundary first, then gives the disk back behind it: everything the reclaim touches is
    /// already declared absent, so interrupting it leaves the node honest and merely fat.</summary>
    private void PruneBlocksAndReceipts(ulong upperExclusive, CancellationToken cancellationToken)
    {
        ulong target = ulong.Min(upperExclusive, _blockTree.SyncPivot.BlockNumber);

        if (target > _blocksDeletePointer)
        {
            VerifyReclaimSupported();
            UpdateBlocksDeletePointer(target);
            SaveDeletePointers();
        }

        // Chases the published boundary, not the cutoff: they part company the moment a pass is interrupted.
        ulong limit = ulong.Min(_blocksDeletePointer, _blockTree.SyncPivot.BlockNumber);
        ulong start = ulong.Max(_blocksReclaimCursor, _minDeletableBlockNumber);
        if (start >= limit) return;

        ulong reclaimed = 0;
        try
        {
            for (ulong from = start; from < limit;)
            {
                // A pass whose budget was already spent on arrival still has to move, but it does not have to move a
                // full chunk. The only way a pass sees a spent token is the scheduler running an expired activity,
                // which it does while block processing is active - and a chunk now unlinks SST files as well as
                // writing tombstones. So the first chunk of such a pass is a fraction of the usual one: enough that
                // progress cannot reach zero, small enough not to sit in front of a block.
                ulong step = reclaimed == 0 && cancellationToken.IsCancellationRequested
                    ? MinimumReclaimChunkBlocks
                    : ReclaimChunkBlocks;

                ulong to = ulong.Min(from + step, limit);
                _blockTree.DeleteOldBlockRange(from, to);
                _receiptStorage.RemoveReceiptsRange(from, to);
                _blockAccessListStore.DeleteRange(from, to);

                _blocksReclaimCursor = to;
                if (_balsDeletePointer < to)
                {
                    Metrics.BlockAccessListHeightsReclaimed += (long)(to - ulong.Max(from, _balsDeletePointer));
                    _balsDeletePointer = to;
                    Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;
                }

                SaveDeletePointers();

                reclaimed += to - from;
                Metrics.BlockHeightsReclaimed += (long)(to - from);
                if (_logger.IsInfo) _logger.Info($"Reclaimed historical blocks #{from} to #{to - 1}, {limit.SaturatingSub(to)} remaining.");
                from = to;

                // Checked after a chunk, not before one. The scheduler stamps its deadline at enqueue, so a pass that
                // waited behind others arrives with the budget already spent - and a token checked first would then
                // reclaim nothing, on every pass, while the boundary kept advancing.
                if (from < limit && cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info(
                        $"Historical block reclaim interrupted at #{from}; the boundary is already published at #{limit} and the next pass resumes from here. Reclaimed {reclaimed} blocks.");
                    return;
                }
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

    /// <summary>Asks each store whether it can range delete, using an empty range so the question changes nothing.
    /// Discovering it after the boundary is published would leave the node announcing a floor it can never reclaim
    /// behind, on every pass, forever.</summary>
    private void VerifyReclaimSupported()
    {
        _blockTree.DeleteOldBlockRange(0, 0);
        _receiptStorage.RemoveReceiptsRange(0, 0);
        _blockAccessListStore.DeleteRange(0, 0);
    }

    private void SweepTransactionIndex(CancellationToken cancellationToken)
    {
        // No token check: this pass runs last, so it is the one most likely to arrive with the budget already gone,
        // and refusing to start there is how it would end up never running at all. The walk honours the token itself,
        // after a minimum slice, on the same rule as the reclaim chunks.
        if (_blocksDeletePointer <= _minDeletableBlockNumber) return;

        LoadTxIndexSweepCursor();

        byte[]? next;
        int removed;
        try
        {
            next = _receiptStorage.SweepTransactionIndex(
                _blocksDeletePointer, _txIndexSweepCursor, TxIndexSweepEntriesPerPass, cancellationToken, out removed);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Isolated from the passes around it: this one walks a column and decodes what it finds, so it has
            // failure modes they do not, and none of them should cost a reclaim that has nothing to do with it.
            // Cancellation is not one of those - it means the caller wants to stop, so it is left to propagate.
            if (_logger.IsWarn) _logger.Warn($"Transaction index sweep failed and will resume next pass: {e.Message}");
            return;
        }

        Metrics.TransactionIndexEntriesPruned += removed;

        if (Bytes.AreEqual(_txIndexSweepCursor, next)) return;

        // Empty value, not a removal: it reads back the same way a missing key would.
        _txIndexSweepCursor = next;
        _metadataDb.Set(MetadataDbKeys.HistoryPruningTxIndexSweepCursor, next ?? []);
    }

    private void LoadTxIndexSweepCursor()
    {
        if (_txIndexSweepCursorLoaded) return;

        byte[]? stored = _metadataDb.Get(MetadataDbKeys.HistoryPruningTxIndexSweepCursor);
        _txIndexSweepCursor = stored is { Length: > 0 } ? stored : null;
        _txIndexSweepCursorLoaded = true;
    }

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
                ulong to = ulong.Min(from + ReclaimChunkBlocks, limit);
                _blockAccessListStore.DeleteRange(from, to);

                _balsDeletePointer = to;
                Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;
                SaveDeletePointers();

                reclaimed += to - from;
                Metrics.BlockAccessListHeightsReclaimed += (long)(to - from);
                from = to;

                // After a chunk, for the same reason as the block pass: a spent budget must cost the tail of a pass,
                // never all of it.
                if (from < limit && cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Block access list reclaim interrupted at #{from}. Reclaimed {reclaimed} access lists.");
                    return;
                }
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
        // Absent on a database pruned by the per-block code, where everything below the boundary is already gone.
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

        // Cursor first, and load-bearing: these are independent writes, and a restart that finds a boundary with no
        // cursor reads it as "already level" and treats an unreclaimed backlog as finished, forever.
        if (_blocksReclaimCursor != _lastSavedBlocksReclaimCursor)
        {
            _metadataDb.Set(MetadataDbKeys.HistoryPruningReclaimCursor, Rlp.Encode(_blocksReclaimCursor).Bytes);
            _lastSavedBlocksReclaimCursor = _blocksReclaimCursor;
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

    private void UpdateBlocksDeletePointer(ulong newDeletePointer)
    {
        _blocksDeletePointer = newDeletePointer;
        Metrics.OldestStoredBlockNumber = _blocksDeletePointer;
        _blockTree.NewOldestBlock(_blocksDeletePointer);
        // Header, not body: headers are never pruned, so this cannot depend on data the reclaim is about to erase.
        BlockHeader? oldest = _blockTree.FindHeader(_blocksDeletePointer, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        if (oldest is not null)
        {
            _oldestBlockHeader = oldest;
            NewOldestBlock?.Invoke(this, new OnNewOldestBlockArgs(oldest));
        }
    }
}
