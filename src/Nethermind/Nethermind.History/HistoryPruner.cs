// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    private readonly IPrunedReceiptRetention _receiptRetention;
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
        IPrunedReceiptRetention receiptRetention,
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
        _receiptRetention = receiptRetention;
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

                // From the reclaim cursor, not the boundary: the boundary is raised the moment a pass starts, so
                // measuring against it reports the heights this pass added rather than the ones still owed, and the
                // silence below then hides a backlog of millions on a node whose head only moves the boundary once
                // an epoch. The access-list figure needs no such care - its pointer is its cursor.
                ulong blocksRemaining = blockUpper.SaturatingSub(_blocksReclaimCursor);
                ulong balsRemaining = balUpper.SaturatingSub(_balsDeletePointer);
                // Silent when only the sweep has work, which is most passes once a cycle is running: announcing two
                // estimates of zero reads as "nothing to do" on the pass that does have some.
                if (_logger.IsInfo && (blocksRemaining > 0 || balsRemaining > 0))
                {
                    _logger.Info($"Pruning historical blocks up to #{blockUpper} ({blocksRemaining} estimated) and block access lists up to #{balUpper} ({balsRemaining} estimated).");
                }

                PruneBlocksAndReceipts(blockUpper, cancellationToken);
                PruneBlockAccessLists(balUpper, cancellationToken);

                // Last: the only pass whose cost its range does not bound, so ahead of the others it would take the
                // whole timeout every pass and starve them.
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
            || (balCutoff is { } balC && _balsDeletePointer < balC)
            // A sweep left part-way through the column is owed too. Without this it only ever resumes while some
            // other pass happens to be due, which is not a property either of them promises the other. Note this
            // schedules resuming a cycle, not starting one: a completed cycle clears the cursor, and the next is
            // still started incidentally by the access-list clause above, which is rolling in every mode.
            || _txIndexSweepCursor is not null;
    }

    private bool PruningIntervalHasElapsed()
        => _pruningInterval == 0 || _blockTree.Head!.Number % _pruningInterval == 0;

    private const ulong ReclaimChunkBlocks = 1_000_000;

    private const ulong MinimumReclaimChunkBlocks = 100_000;

    /// <summary>One retained height in this many is where reclaiming the gaps by range stops being worth the writes
    /// it costs. Not a tuning knob: either side of it is correct, and only the cost differs.</summary>
    private const int DenseRetentionDivisor = 8;

    /// <summary>How far the receipt walk goes before it will look at the deadline again. A chunk is sized for range
    /// operations, which are a handful per chunk however wide it is; deciding retention can cost a header read per
    /// height, so the same width would hold the pass for as long as that takes and answer to no deadline. Small
    /// enough that a slice is bounded work, large enough that the check is not the work.</summary>
    private const ulong ReceiptRetentionSlice = 10_000;

    /// <summary>
    /// Heights the next chunk covers, reduced when the token is already spent so progress cannot reach zero without
    /// a full chunk of tombstones and file unlinks landing in front of a block. Draining that way is slow, which is
    /// right: a node in that state has no room to prune. The access-list loop mostly reaches the reduced step
    /// because the block loop spent the budget legitimately, which is accepted - during a drain only the sliver
    /// ahead of the block cursor is left for it, and afterwards it gets full steps again.
    /// </summary>
    private static ulong ChunkStep(ulong reclaimed, CancellationToken cancellationToken) =>
        reclaimed == 0 && cancellationToken.IsCancellationRequested ? MinimumReclaimChunkBlocks : ReclaimChunkBlocks;

    /// <summary>Ceiling on entries examined per pass, not what governs the rate: with a non-zero
    /// <c>PruningTimeoutSeconds</c> the token ends the walk first, so the index settles at some stale fraction of
    /// the live set rather than at zero.</summary>
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
                ulong to = ulong.Min(from + ChunkStep(reclaimed, cancellationToken), limit);

                // Receipts first: retaining a block's receipts re-encodes them from the block, which the delete
                // below is about to remove. Where the retention has to read headers this can stop short, and the
                // rest of the chunk has to stop with it - deleting bodies past that point would strand receipts
                // whose retention was never decided.
                to = RetainReceiptsAndReclaimTheRest(from, to, cancellationToken);

                _blockTree.DeleteOldBlockRange(from, to);
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

                // After a chunk, not before one: the deadline is stamped at enqueue, so a pass that waited behind
                // others arrives spent, and checking first would reclaim nothing while the boundary kept advancing.
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

    /// <summary>
    /// Reclaims the receipts of <c>[from, to)</c> while keeping the ones something on this node still answers for,
    /// and returns the height it actually reached - which is <paramref name="to"/> unless the budget ran out first.
    /// </summary>
    /// <remarks>
    /// Walked in slices so the caller's deadline can land between two of them. A slice inside the part the retention
    /// answered for costs nothing to decide; outside it, one header is read per height. Either way only the heights
    /// that come back as candidates are read as blocks, and everything between them goes back by range.
    /// </remarks>
    private ulong RetainReceiptsAndReclaimTheRest(ulong from, ulong to, CancellationToken cancellationToken)
    {
        IReadOnlySet<ulong> answered = _receiptRetention.RetainedHeights(from, to, out ulong answeredFrom, out ulong answeredTo);

        for (ulong cursor = from; cursor < to;)
        {
            ulong sliceEnd = ulong.Min(cursor + ReceiptRetentionSlice, to);

            // A slice must not straddle the edge of what the retention answered: on one side the retained heights
            // are already known, on the other they have to be read from headers, and one slice cannot be both.
            if (cursor < answeredFrom) sliceEnd = ulong.Min(sliceEnd, answeredFrom);
            else if (cursor < answeredTo) sliceEnd = ulong.Min(sliceEnd, answeredTo);

            bool alreadyAnswered = cursor >= answeredFrom && cursor < answeredTo;
            ReclaimReceiptSlice(cursor, sliceEnd, alreadyAnswered ? answered : null);
            cursor = sliceEnd;

            if (cursor < to && cancellationToken.IsCancellationRequested) return cursor;
        }

        return to;
    }

    /// <summary>Reclaims one slice. <paramref name="answered"/> is null where the retention could not answer for the
    /// span, which is where the headers have to be read instead.</summary>
    private void ReclaimReceiptSlice(ulong fromInclusive, ulong toExclusive, IReadOnlySet<ulong>? answered)
    {
        List<ulong> candidates = answered is null
            ? CandidatesFromHeaders(fromInclusive, toExclusive)
            : CandidatesFromAnswer(answered, fromInclusive, toExclusive);

        if (candidates.Count == 0)
        {
            _receiptStorage.RemoveReceiptsRange(fromInclusive, toExclusive);
            return;
        }

        // Dense retention makes a range removal per gap the wrong shape: the gaps are a block or two wide, no file
        // ever lies entirely inside one, and every attempt still costs a write and an unlink that cannot find
        // anything. Slicing a busy contract puts the slice here, and the density is measured rather than assumed.
        if (candidates.Count * DenseRetentionDivisor >= (long)(toExclusive - fromInclusive))
        {
            HashSet<ulong> keep = [.. candidates];
            for (ulong number = fromInclusive; number < toExclusive; number++)
            {
                if (keep.Contains(number) && TryRetainAt(number)) continue;

                // A height whose level or body will not load still has to lose its receipts: the block range is
                // deleted either way, and a row left behind has nothing to be read against and nothing naming it.
                if (!TryRemoveReceiptsAt(number)) _receiptStorage.RemoveReceiptsRange(number, number + 1);
            }

            return;
        }

        candidates.Sort();

        ulong gapStart = fromInclusive;
        foreach (ulong height in candidates)
        {
            if (height > gapStart) _receiptStorage.RemoveReceiptsRange(gapStart, height);

            // A height the retention named but whose receipts could not be made self-describing keeps nothing:
            // leaving them would be receipts that outlive the body they need to be read.
            if (!TryRetainAt(height)) _receiptStorage.RemoveReceiptsRange(height, height + 1);
            gapStart = height + 1;
        }

        if (gapStart < toExclusive) _receiptStorage.RemoveReceiptsRange(gapStart, toExclusive);
    }

    private static List<ulong> CandidatesFromAnswer(IReadOnlySet<ulong> answered, ulong fromInclusive, ulong toExclusive)
    {
        List<ulong> candidates = [];
        foreach (ulong height in answered)
        {
            if (height >= fromInclusive && height < toExclusive) candidates.Add(height);
        }

        return candidates;
    }

    /// <summary>
    /// Heights whose bloom says their receipts might be worth keeping. The retention decides from the header, so this
    /// costs a header read per height rather than a body read, and hands back a set sparse enough for the rest of the
    /// slice to go by range. A bloom false positive only retains a height that did not need it.
    /// </summary>
    private List<ulong> CandidatesFromHeaders(ulong fromInclusive, ulong toExclusive)
    {
        List<ulong> candidates = [];
        for (ulong number = fromInclusive; number < toExclusive; number++)
        {
            ChainLevelInfo? level = _chainLevelInfoRepository.LoadLevel(number);
            if (level is null) continue;

            foreach (BlockInfo info in level.BlockInfos)
            {
                BlockHeader? header = _blockTree.FindHeader(info.BlockHash, BlockTreeLookupOptions.None, number);
                if (header is null) continue;

                if (_receiptRetention.ShouldRetainReceipts(header))
                {
                    candidates.Add(number);
                    break;
                }
            }
        }

        return candidates;
    }

    /// <summary>Removes by block, which is cheaper than a range of one height. False when the level or every body is
    /// missing, so the caller can fall back to the range and never leave a row behind.</summary>
    private bool TryRemoveReceiptsAt(ulong number)
    {
        ChainLevelInfo? level = _chainLevelInfoRepository.LoadLevel(number);
        if (level is null) return false;

        // Every body at the height, not merely one: the range the caller falls back to covers the height whatever its
        // hashes are, while removing by block can only reach the ones that loaded.
        bool removedAll = level.BlockInfos.Length > 0;
        foreach (BlockInfo info in level.BlockInfos)
        {
            Block? block = _blockTree.FindBlock(info.BlockHash, BlockTreeLookupOptions.None, number);
            if (block is null)
            {
                removedAll = false;
                continue;
            }

            _receiptStorage.RemoveReceipts(block);
        }

        return removedAll;
    }

    private bool TryRetainAt(ulong number)
    {
        ChainLevelInfo? level = _chainLevelInfoRepository.LoadLevel(number);
        if (level is null) return false;

        bool any = false;
        foreach (BlockInfo info in level.BlockInfos)
        {
            Block? block = _blockTree.FindBlock(info.BlockHash, BlockTreeLookupOptions.None, number);
            if (block is null) continue;

            if (_receiptStorage.TryRetainSelfDescribing(block))
            {
                Metrics.SlicedReceiptsRetained++;
                any = true;
            }
        }

        return any;
    }

    /// <summary>Asks each store whether it can range delete, using an empty range so the question changes nothing.
    /// Found out after the boundary is published, it would mean announcing a floor nothing can reclaim behind.</summary>
    private void VerifyReclaimSupported()
    {
        _blockTree.DeleteOldBlockRange(0, 0);
        _receiptStorage.RemoveReceiptsRange(0, 0);
        _blockAccessListStore.DeleteRange(0, 0);
    }

    private void SweepTransactionIndex(CancellationToken cancellationToken)
    {
        // No token check: running last, this is the pass most likely to arrive with the budget gone, and refusing to
        // start there is how it ends up never running. The walk honours the token after a minimum slice instead.
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
            // Isolated: this pass walks and decodes, so it has failure modes the reclaims do not, and none of them
            // should cost a reclaim. Cancellation is not one of them and propagates.
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
                ulong to = ulong.Min(from + ChunkStep(reclaimed, cancellationToken), limit);
                _blockAccessListStore.DeleteRange(from, to);

                _balsDeletePointer = to;
                Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;
                SaveDeletePointers();

                reclaimed += to - from;
                Metrics.BlockAccessListHeightsReclaimed += (long)(to - from);
                from = to;

                // After a chunk, for the same reason as the block pass.
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

        // Loaded here rather than lazily in the sweep, because ShouldPruneHistory has to see it: a sweep left
        // half-finished is work owed, and if nothing else were owed the pass would never run to notice.
        LoadTxIndexSweepCursor();

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
