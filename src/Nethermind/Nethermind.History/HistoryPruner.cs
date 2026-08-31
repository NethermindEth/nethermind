// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
    private readonly IHeaderStore _headerStore;
    private readonly IDb _metadataDb;
    private readonly IDb _blocksDb;
    private static readonly byte[] LegacyLowestInsertedBodyNumberKey = ((long)0).ToBigEndianByteArrayWithoutLeadingZeros();
    private readonly ulong _persistedUnreclaimedFloor;
    private readonly IProcessExitSource _processExitSource;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly IHistoryConfig _historyConfig;
    private readonly IPrunedReceiptRetention _receiptRetention;
    private readonly bool _enabled;
    private readonly ulong _pruningInterval;
    private readonly ulong _minHistoryRetentionEpochs;
    private readonly ulong _minBalRetentionEpochs;
    private readonly ulong _ancientBarrier;
    private readonly ulong _ancientReceiptsBarrier;
    private readonly bool _fastSync;
    private readonly ISyncConfig _syncConfig;
    private static readonly TimeSpan AncientHoldRelogInterval = TimeSpan.FromMinutes(10);
    private long _ancientHoldLastLogged;
    private bool _frontierFrozen;
    private readonly IDb _defaultReceiptsColumn;
    private readonly ulong _minDeletableBlockNumber;

    private ulong _blocksDeletePointer = 1;
    private ulong _blocksReclaimCursor = 1;
    private byte[]? _txIndexSweepCursor;
    private bool _txIndexSweepCursorLoaded;
    private readonly Dictionary<ulong, (ulong[] Bitmap, ulong AnsweredFrom, ulong AnsweredTo)> _sweepBuckets = [];
    private readonly Dictionary<ulong, bool> _sweepBloomDecisions = [];
    private Func<ulong, bool>? _sweepRetentionLookup;
    private ulong _balsDeletePointer = 1;
    private ulong _lastSavedBlocksDeletePointer = 1;
    private ulong _lastSavedBlocksReclaimCursor = 1;
    private ulong _sliceCleanupCursor = 1;
    private ulong _lastSavedSliceCleanupCursor = 1;
    private ulong _lastSavedBalsDeletePointer = 1;
    // Read by JSON-RPC and the sync server while the pruner writes them under a lock it can hold for a whole reclaim.
    private volatile BlockHeader? _oldestBlockHeader;
    private volatile bool _hasLoadedDeletePointers;
    private volatile bool _stampsValidated;
    private int _currentlyPruning;

    public event EventHandler<OnNewOldestBlockArgs>? NewOldestBlock;

    public class HistoryPrunerException(string message, Exception? innerException = null) : Exception(message, innerException);

    public HistoryPruner(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        IBlockAccessListStore blockAccessListStore,
        ISpecProvider specProvider,
        IChainLevelInfoRepository chainLevelInfoRepository,
        IHeaderStore headerStore,
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
        _headerStore = headerStore;
        _metadataDb = dbProvider.MetadataDb;
        _blocksDb = dbProvider.BlocksDb;
        _processExitSource = processExitSource;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _historyConfig = historyConfig;
        _receiptRetention = receiptRetention;
        _enabled = historyConfig.Enabled();
        _pruningInterval = historyConfig.PruningInterval * SlotsPerEpoch;
        _minHistoryRetentionEpochs = specProvider.GenesisSpec.MinHistoryRetentionEpochs;
        _minBalRetentionEpochs = specProvider.GenesisSpec.MinBalRetentionEpochs;
        _ancientReceiptsBarrier = syncConfig.AncientReceiptsBarrierCalc;
        _fastSync = syncConfig.FastSync;
        _syncConfig = syncConfig;
        _defaultReceiptsColumn = dbProvider.ReceiptsDb.GetColumnDb(ReceiptsColumns.Default);
        _minDeletableBlockNumber = (_blockTree.Genesis?.Number ?? 0) + 1; // do not remove genesis

        (ulong? persistedPointer, ulong? persistedCursor) = ReadPersistedPointers(_metadataDb);
        ulong snapshotPointer = persistedPointer ?? 0;
        ulong barrierFloor = _blockTree.GetLowestBlock(); // still the config barrier here - nothing has raised it yet
        // An absent cursor defaults to the pointer, as at load: on a per-block-pruned database everything below the boundary is gone.
        _persistedUnreclaimedFloor = ulong.Max(ulong.Min(persistedCursor ?? snapshotPointer, snapshotPointer), barrierFloor);

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

    // Deliberately lock-free - this sits on the eth_getLogs path, and the first pruning pass or
    // OldestBlockHeader access drives the load, which the ancient-bodies hold can defer for the whole
    // backfill. Until then the answer is the later of the constructor's snapshot of the persisted cursors
    // - exact, since nothing moves them in this process before the load - and the configured barrier:
    // refusing more than necessary beats serving a reclaimed height silently short.
    public ulong OldestUnreclaimedBlockNumber =>
        _hasLoadedDeletePointers ? ulong.Min(_blocksReclaimCursor, _blocksDeletePointer) : _persistedUnreclaimedFloor;

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
            (_hasLoadedDeletePointers && _stampsValidated && !ShouldPruneHistory()))
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
                if (!TryLoadDeletePointers())
                {
                    SkipLocalPruning();
                    return;
                }

                // Before the interval gate: the read side refuses every sliced address until this has validated
                // the stamps, and the first interval boundary can be most of an hour away. The flag keeps the
                // uncontended fast path honest - another caller loading the pointers must not skip this tick.
                // A frozen frontier must not stamp: the stamp never lowers, and this one would cap slice
                // coverage at a mid-backfill depth forever.
                if (!_frontierFrozen)
                {
                    _receiptRetention.OnPruningPassStarting(OldestStoredReceipts(), _blocksReclaimCursor, _sliceCleanupCursor);
                    _stampsValidated = true;
                }

                if (!ShouldPruneHistory())
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

                // From the cursor, not the boundary: the boundary is raised before any reclaim happens.
                ulong blocksRemaining = blockUpper.SaturatingSub(_blocksReclaimCursor);
                ulong balsRemaining = balUpper.SaturatingSub(_balsDeletePointer);
                // Silent when only the sweep has work, which is most passes once a cycle is running: announcing two
                // estimates of zero reads as "nothing to do" on the pass that does have some.
                if (_logger.IsInfo && (blocksRemaining > 0 || balsRemaining > 0))
                {
                    _logger.Info($"Pruning historical blocks up to #{blockUpper} ({blocksRemaining} estimated) and block access lists up to #{balUpper} ({balsRemaining} estimated).");
                }

                PruneBlocksAndReceipts(blockUpper, cancellationToken);
                CleanupExpiredSliceRetention(cancellationToken);
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
        // While the ancient bodies feed is still descending, the oldest existing body is the download
        // frontier, not the truth - a pointer latched from it would over-report forever.
        bool frontierFrozen = false;
        if (AncientBodiesStillDownloading())
        {
            if (_syncConfig.SynchronizationEnabled)
            {
                return false;
            }

            // With synchronization disabled no feed can move the frontier, so it is the truth for this
            // process - but it is never persisted, so the next syncing process re-derives the boundary.
            frontierFrozen = true;
        }

        ulong? oldestBlockNumber = BlockTree.BinarySearchBlockNumber(
            _minDeletableBlockNumber,
            _blockTree.SyncPivot.BlockNumber,
            BlockExists,
            BlockTree.BinarySearchDirection.Down);

        if (oldestBlockNumber is not null)
        {
            UpdateBlocksDeletePointer(oldestBlockNumber.Value);
            if (frontierFrozen)
            {
                _frontierFrozen = true;
                _lastSavedBlocksDeletePointer = _blocksDeletePointer;
            }
            else
            {
                SaveDeletePointers();
            }
            return true;
        }

        return false;
    }

    private bool AncientBodiesStillDownloading()
    {
        // Keyed on the tree pivot, like the feed itself: a CL-discovered pivot never reaches the sync config.
        ulong pivot = _blockTree.SyncPivot.BlockNumber;
        if (!_fastSync || pivot == 0 || !_syncConfig.DownloadBodiesInFastSync)
        {
            return false;
        }

        // The feed persists a completion marker when it reaches its own latched barrier, so the release
        // never has to reconstruct a barrier that drifts with the pruning cutoff. The static barrier is a
        // term of the feed's ComputeBarrier and so always releasable, and a pointer parked at the barrier
        // the feed recorded when it last started is a finished descent even if the marker predates this
        // build - only for a descent whose barrier never moved, since a rolling cutoff climbs during a
        // long descent and parks the pointer above the recorded value; a legacy database self-heals
        // regardless, because the feed activates once and writes the marker. A written pointer above
        // those with no marker is a descent in progress - the persisted pointer only moves every flush
        // interval, so its quietness proves nothing and the hold stays; an absent pointer is a feed that
        // has not run yet, which a synchronizing process eventually runs.
        if (_metadataDb.KeyExists(MetadataDbKeys.AncientBodiesDownloadComplete))
        {
            return false;
        }

        byte[]? pointerBytes = _metadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber) ?? _blocksDb.Get(LegacyLowestInsertedBodyNumberKey);
        ulong? pointer = pointerBytes is null ? null : new RlpReader(pointerBytes).DecodeULong();
        ulong barrier = ulong.Max(1UL, ulong.Min(pivot, _syncConfig.AncientBodiesBarrier));
        if (pointer <= barrier)
        {
            return false;
        }

        byte[]? barrierWhenStartedBytes = _metadataDb.Get(MetadataDbKeys.BodiesBarrierWhenStarted);
        if (pointer is not null && barrierWhenStartedBytes is not null)
        {
            ulong barrierWhenStarted = barrierWhenStartedBytes.AsSpan().ToULongFromBigEndianByteArrayWithoutLeadingZeros();
            // Only while the feed still targets at least that barrier: a config edit that lowered it reopens
            // the descent, and the recorded value then describes the descent that finished, not the current one.
            if (barrierWhenStarted <= ulong.Max(barrier, CutoffBlockNumber ?? 0) && pointer <= barrierWhenStarted)
            {
                return false;
            }
        }

        if (_syncConfig.SynchronizationEnabled && (_ancientHoldLastLogged == 0 || Stopwatch.GetElapsedTime(_ancientHoldLastLogged) >= AncientHoldRelogInterval))
        {
            _ancientHoldLastLogged = Stopwatch.GetTimestamp();
            // Warn only when pruning is on - there the hold means unbounded disk growth; with pruning off it
            // merely defers boundary discovery, and a healthy default-config sync must not emit alarm output.
            string holdMessage = $"Holding the history boundary while the ancient bodies backfill is descending (currently #{pointer?.ToString() ?? "none"}).";
            if (_enabled)
            {
                if (_logger.IsWarn) _logger.Warn(holdMessage);
            }
            else if (_logger.IsInfo)
            {
                _logger.Info(holdMessage);
            }
        }

        return true;
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
            || _txIndexSweepCursor is not null
            || SliceCleanupTarget() > ulong.Max(_sliceCleanupCursor, _minDeletableBlockNumber);
    }

    private bool PruningIntervalHasElapsed()
        => _pruningInterval == 0 || _blockTree.Head!.Number % _pruningInterval == 0;

    private const ulong ReclaimChunkBlocks = 1_000_000;

    private const ulong MinimumReclaimChunkBlocks = 100_000;

    /// <summary>Density above which the gaps are too narrow for a range to pay for itself, so the heights go one
    /// at a time instead. Below it a range covers many heights at once and lets whole files be unlinked.</summary>
    private const int DenseRetentionDivisor = 8;

    /// <summary>How far the receipt walk goes before it looks at the deadline again. A chunk is sized for range
    /// operations; deciding retention can cost a header read per height, so it needs its own, narrower step.</summary>
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

                // It can stop short, and the rest of the chunk stops with it rather than stranding undecided heights.
                to = RetainSlicedAndReclaimTheRest(from, to, cancellationToken);

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

    /// <summary>Reclaims <c>[from, to)</c> except the heights still answered for, and returns the
    /// height it reached - <paramref name="to"/> unless the budget ran out between two slices.</summary>
    private ulong RetainSlicedAndReclaimTheRest(ulong from, ulong to, CancellationToken cancellationToken, bool meterRetained = true)
    {
        IReadOnlySet<ulong> answered = _receiptRetention.RetainedHeights(from, to, out ulong answeredFrom, out ulong answeredTo);

        // Sorted once, so each slice takes its own segment by binary search instead of filtering the whole set.
        using ArrayPoolList<ulong> sortedAnswered = new(answered.Count);
        foreach (ulong answeredHeight in answered) sortedAnswered.Add(answeredHeight);
        sortedAnswered.AsSpan().Sort();

        // A single wide range beats a hundred narrow ones, which unlink fewer whole files between them.
        if (answeredFrom <= from && answeredTo >= to && (ulong)answered.Count <= ReceiptRetentionSlice)
        {
            ReclaimSlice(from, to, sortedAnswered, meterRetained);
            return to;
        }

        for (ulong cursor = from; cursor < to;)
        {
            ulong sliceEnd = ulong.Min(cursor + ReceiptRetentionSlice, to);

            // A slice cannot straddle the edge of what was answered: one side is known, the other needs headers.
            if (cursor < answeredFrom) sliceEnd = ulong.Min(sliceEnd, answeredFrom);
            else if (cursor < answeredTo) sliceEnd = ulong.Min(sliceEnd, answeredTo);

            bool alreadyAnswered = cursor >= answeredFrom && cursor < answeredTo;
            ReclaimSlice(cursor, sliceEnd, alreadyAnswered ? sortedAnswered : null, meterRetained);
            cursor = sliceEnd;

            // Slices decide where a pass may stop, not how early. ChunkStep already narrowed the chunk to the
            // drain floor for a pass that arrived spent, so stopping at the first slice boundary would cut that
            // floor by another order of magnitude and leave a backlog draining ten times slower than promised.
            if (cursor < to
                && cursor - from >= MinimumReclaimChunkBlocks
                && cancellationToken.IsCancellationRequested) return cursor;
        }

        return to;
    }

    /// <summary><paramref name="sortedAnswered"/> is null where the headers have to be read instead.</summary>
    private void ReclaimSlice(ulong fromInclusive, ulong toExclusive, ArrayPoolList<ulong>? sortedAnswered, bool meterRetained = true)
    {
        IOwnedReadOnlyList<ChainLevelInfo?>? levels = null;
        try
        {
            using ArrayPoolList<ulong> candidates = sortedAnswered is null
                ? CandidatesFromLevels(fromInclusive, toExclusive, levels = LoadLevels(fromInclusive, toExclusive))
                : CandidatesFromAnswer(sortedAnswered.AsSpan(), fromInclusive, toExclusive);

            if (candidates.Count == 0)
            {
                ReclaimBoth(fromInclusive, toExclusive);
                return;
            }

            if (meterRetained) Metrics.SlicedReceiptsRetained += candidates.Count;

            candidates.AsSpan().Sort();

            if (candidates.Count * DenseRetentionDivisor >= (long)(toExclusive - fromInclusive))
            {
                levels ??= LoadLevels(fromInclusive, toExclusive);
                using ArrayPoolList<(ulong FromInclusive, ulong ToExclusive)> unreachable = new(4);
                for (ulong number = fromInclusive; number < toExclusive; number++)
                {
                    if (candidates.AsSpan().BinarySearch(number) >= 0) continue;

                    int index = (int)(number - fromInclusive);
                    ChainLevelInfo? level = index < levels.Count ? levels[index] : null;

                    // A height whose level will not load has to lose both whatever its hashes are.
                    if (!RemoveBothAt(number, level))
                    {
                        int last = unreachable.Count - 1;
                        if (last >= 0 && unreachable[last].ToExclusive == number) unreachable[last] = (unreachable[last].FromInclusive, number + 1);
                        else unreachable.Add((number, number + 1));
                    }
                }

                if (unreachable.Count != 0)
                {
                    _receiptStorage.RemoveReceiptsRanges(unreachable);
                    _blockTree.DeleteOldBlockRanges(unreachable);
                }

                return;
            }

            // Sparse: the gaps between retained heights are wide, so one range each beats walking them, and a
            // range is what lets whole files be unlinked instead of waiting for compaction.
            using ArrayPoolList<(ulong FromInclusive, ulong ToExclusive)> gaps = new(candidates.Count + 1);
            ulong gapStart = fromInclusive;
            for (int i = 0; i < candidates.Count; i++)
            {
                ulong height = candidates[i];
                if (height > gapStart) gaps.Add((gapStart, height));
                gapStart = height + 1;
            }

            if (gapStart < toExclusive) gaps.Add((gapStart, toExclusive));
            if (gaps.Count == 0) return;

            _receiptStorage.RemoveReceiptsRanges(gaps);
            _blockTree.DeleteOldBlockRanges(gaps);
        }
        finally
        {
            levels?.Dispose();
        }
    }

    private void ReclaimBoth(ulong fromInclusive, ulong toExclusive)
    {
        _receiptStorage.RemoveReceiptsRange(fromInclusive, toExclusive);
        _blockTree.DeleteOldBlockRange(fromInclusive, toExclusive);
    }

    /// <summary>False when the level names nothing, so the caller ranges the height instead. A present level is
    /// trusted to name every stored block at its height, as every canonical read does - a body stored outside its
    /// level survives here, where the range form would drop it.</summary>
    private bool RemoveBothAt(ulong number, ChainLevelInfo? level)
    {
        if (level is null || level.BlockInfos.Length == 0) return false;

        foreach (BlockInfo info in level.BlockInfos)
        {
            _receiptStorage.RemoveReceipts(number, info.BlockHash);
            _blockTree.DeleteOldBlock(number, info.BlockHash);
        }

        return true;
    }

    private IOwnedReadOnlyList<ChainLevelInfo?> LoadLevels(ulong fromInclusive, ulong toExclusive)
    {
        using ArrayPoolListRef<ulong> numbers = new((int)(toExclusive - fromInclusive));
        for (ulong number = fromInclusive; number < toExclusive; number++) numbers.Add(number);

        return _chainLevelInfoRepository.MultiLoadLevel(numbers);
    }

    private static ArrayPoolList<ulong> CandidatesFromAnswer(ReadOnlySpan<ulong> sortedAnswered, ulong fromInclusive, ulong toExclusive)
    {
        int lower = LowerBound(sortedAnswered, fromInclusive);
        int upper = LowerBound(sortedAnswered, toExclusive);

        ArrayPoolList<ulong> candidates = new(upper - lower);
        candidates.AddRange(sortedAnswered[lower..upper]);
        return candidates;
    }

    private static int LowerBound(ReadOnlySpan<ulong> sorted, ulong value)
    {
        int index = sorted.BinarySearch(value);
        return index < 0 ? ~index : index;
    }

    /// <summary>Heights whose bloom says their receipts might be worth keeping, over the levels already read in bulk
    /// and one sequential header pass rather than two random reads a height. A false positive only over-retains.</summary>
    private ArrayPoolList<ulong> CandidatesFromLevels(ulong fromInclusive, ulong toExclusive, IOwnedReadOnlyList<ChainLevelInfo?> levels)
    {
        ArrayPoolList<ulong> candidates = new(64);
        Dictionary<ValueHash256, BlockHeader> prefetched = _headerStore.PrefetchByNumberRange(fromInclusive, toExclusive);

        for (int i = 0; i < levels.Count; i++)
        {
            ChainLevelInfo? level = levels[i];
            if (level is null) continue;

            ulong number = fromInclusive + (ulong)i;
            foreach (BlockInfo info in level.BlockInfos)
            {
                if (!prefetched.TryGetValue(info.BlockHash.ValueHash256, out BlockHeader? header))
                    header = _blockTree.FindHeader(info.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing, number);

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

    /// <summary>Asks each store whether it can range delete, using an empty range so the question changes nothing.
    /// Found out after the boundary is published, it would mean announcing a floor nothing can reclaim behind.</summary>
    private void VerifyReclaimSupported()
    {
        _blockTree.DeleteOldBlockRange(0, 0);
        _receiptStorage.RemoveReceiptsRange(0, 0);
        _blockAccessListStore.DeleteRange(0, 0);
    }

    /// <summary>Heights a bounded slice retained while they were inside its window fall out of it as the head
    /// advances, and the main reclaim cursor never returns to them - this cursor does, re-asking the retention over
    /// the expired band so what no entry still claims is reclaimed after all.</summary>
    private void CleanupExpiredSliceRetention(CancellationToken cancellationToken)
    {
        ulong target = SliceCleanupTarget();
        ulong start = ulong.Max(_sliceCleanupCursor, _minDeletableBlockNumber);
        if (start >= target) return;

        // One minimum chunk, not a full one: this runs inside a pass whose budget the main reclaim already spent,
        // so it adds at most the same uninterruptible floor every other consumer of the pass accepts.
        ulong to = ulong.Min(target, start + MinimumReclaimChunkBlocks);
        ulong reached = RetainSlicedAndReclaimTheRest(start, to, cancellationToken, meterRetained: false);
        if (reached > _sliceCleanupCursor)
        {
            _sliceCleanupCursor = reached;
            SaveDeletePointers();
        }

        if (_logger.IsInfo) _logger.Info(
            $"Expired slice retention cleanup reached #{reached}; {target.SaturatingSub(reached)} heights of expired band remain.");
    }

    /// <summary>Only ground the main reclaim has already covered can be cleaned, or the two cursors would race.</summary>
    private ulong SliceCleanupTarget() =>
        ulong.Min(_receiptRetention.ExpiredRetentionUpperBound(), ulong.Min(_blocksReclaimCursor, _blocksDeletePointer));

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
                _blocksDeletePointer, _txIndexSweepCursor, TxIndexSweepEntriesPerPass, SweepRetentionLookup(), cancellationToken, out removed);
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

    /// <summary>Memoized per pass: the sweep walks in hash order, so its blocks land in random buckets and each
    /// bucket's span query is paid once, kept as a bitmap rather than the answer set - a busy contract's set holds
    /// thousands of heights per bucket where the bitmap holds 1.25 KB. A height the index cannot answer for falls
    /// back to its header's bloom, exactly as candidate discovery does, so a node without the log index still
    /// sweeps; a header that no longer resolves is not retained, since nothing can answer for it anyway.</summary>
    internal Func<ulong, bool> SweepRetentionLookup()
    {
        foreach (KeyValuePair<ulong, (ulong[] Bitmap, ulong AnsweredFrom, ulong AnsweredTo)> entry in _sweepBuckets)
        {
            ArrayPool<ulong>.Shared.Return(entry.Value.Bitmap);
        }

        _sweepBuckets.Clear();
        _sweepBloomDecisions.Clear();
        return _sweepRetentionLookup ??= height =>
        {
            ulong bucketStart = height / ReceiptRetentionSlice * ReceiptRetentionSlice;
            if (!_sweepBuckets.TryGetValue(bucketStart, out (ulong[] Bitmap, ulong AnsweredFrom, ulong AnsweredTo) bucket))
            {
                IReadOnlySet<ulong> retained = _receiptRetention.RetainedHeights(
                    bucketStart, bucketStart + ReceiptRetentionSlice, out ulong answeredFrom, out ulong answeredTo);

                const int bitmapLength = (int)((ReceiptRetentionSlice + 63) / 64);
                ulong[] bitmap = ArrayPool<ulong>.Shared.Rent(bitmapLength);
                Array.Clear(bitmap, 0, bitmapLength);
                foreach (ulong retainedHeight in retained)
                {
                    if (retainedHeight < bucketStart || retainedHeight >= bucketStart + ReceiptRetentionSlice) continue;

                    ulong offset = retainedHeight - bucketStart;
                    bitmap[offset / 64] |= 1UL << (int)(offset % 64);
                }

                _sweepBuckets[bucketStart] = bucket = (bitmap, answeredFrom, answeredTo);
            }

            if (height >= bucket.AnsweredFrom && height < bucket.AnsweredTo)
            {
                ulong offset = height - bucketStart;
                return (bucket.Bitmap[offset / 64] & (1UL << (int)(offset % 64))) != 0;
            }

            if (_sweepBloomDecisions.TryGetValue(height, out bool decided)) return decided;

            BlockHeader? header = _blockTree.FindHeader(height,
                BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            bool retainedByBloom = header is not null && _receiptRetention.ShouldRetainReceipts(header);
            _sweepBloomDecisions[height] = retainedByBloom;
            return retainedByBloom;
        };
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

    /// <summary>The oldest height whose receipts this node holds: the delete pointer measures bodies, so this
    /// also consults the receipt backfill's own pointer wherever one was ever persisted - only the absent-pointer
    /// fallback is fast-sync-specific, where it means no ancient receipt has been downloaded yet and the pivot is
    /// the floor.</summary>
    private ulong OldestStoredReceipts()
    {
        ulong receiptsFloor = _defaultReceiptsColumn.Get(Keccak.Zero) is { } lowestInserted
            ? new RlpReader(lowestInserted).DecodeULong()
            : _fastSync ? _blockTree.SyncPivot.BlockNumber : 0;

        return ulong.Max(_blocksDeletePointer, ulong.Max(_ancientReceiptsBarrier, receiptsFloor));
    }

    private static (ulong? Pointer, ulong? Cursor) ReadPersistedPointers(IDb metadataDb)
    {
        byte[]? pointerBytes = metadataDb.Get(MetadataDbKeys.HistoryPruningDeletePointer);
        byte[]? cursorBytes = metadataDb.Get(MetadataDbKeys.HistoryPruningReclaimCursor);
        return (pointerBytes is null ? null : new RlpReader(pointerBytes).DecodeULong(),
            cursorBytes is null ? null : new RlpReader(cursorBytes).DecodeULong());
    }

    private bool TryLoadDeletePointers()
    {
        if (_hasLoadedDeletePointers)
        {
            return true;
        }

        (ulong? persistedPointer, ulong? persistedCursor) = ReadPersistedPointers(_metadataDb);
        if (persistedPointer is null)
        {
            if (!SetDeletePointerToOldestBlock())
            {
                return false;
            }
        }
        else
        {
            UpdateBlocksDeletePointer(ulong.Max(persistedPointer.Value, _minDeletableBlockNumber));
            _lastSavedBlocksDeletePointer = _blocksDeletePointer;
        }

        // Absent on a database pruned by the per-block code, where everything below the boundary is already gone.
        _blocksReclaimCursor = persistedCursor is null
            ? _blocksDeletePointer
            : ulong.Max(persistedCursor.Value, _minDeletableBlockNumber);
        _lastSavedBlocksReclaimCursor = persistedCursor is null ? ulong.MaxValue : _blocksReclaimCursor;

        byte[]? balsVal = _metadataDb.Get(MetadataDbKeys.BlockAccessListPruningDeletePointer);
        // Until BAL pruning runs once, the BAL pointer trails the blocks pointer because BALs are
        // deleted alongside blocks in PruneBlocksAndReceipts. Default to the blocks pointer on first load.
        _balsDeletePointer = balsVal is null
            ? _blocksDeletePointer
            : ulong.Max(new RlpReader(balsVal).DecodeULong(), _blocksDeletePointer);
        // ulong.MaxValue is used as sentinel: guarantees SaveDeletePointers saves on the very first call.
        _lastSavedBalsDeletePointer = balsVal is null ? ulong.MaxValue : _balsDeletePointer;
        Metrics.OldestStoredBlockAccessListBlockNumber = _balsDeletePointer;

        byte[]? cleanupVal = _metadataDb.Get(MetadataDbKeys.HistoryPruningSliceCleanupCursor);
        _sliceCleanupCursor = cleanupVal is null
            ? _minDeletableBlockNumber
            : ulong.Max(new RlpReader(cleanupVal).DecodeULong(), _minDeletableBlockNumber);
        _lastSavedSliceCleanupCursor = cleanupVal is null ? ulong.MaxValue : _sliceCleanupCursor;

        // A frozen frontier must not leak to disk through the first-save sentinels either.
        if (_frontierFrozen)
        {
            _lastSavedBlocksReclaimCursor = _blocksReclaimCursor;
            _lastSavedBalsDeletePointer = _balsDeletePointer;
            _lastSavedSliceCleanupCursor = _sliceCleanupCursor;
        }

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

        // Ahead of the cursor writes: a stop between the two leaves the proof ahead of the cursor, which a resumed
        // retention-aware walk re-covers, where the reverse order reads every ungraceful stop as a lapse. The
        // accepted residual is the mirror: a crash between these two adjacent WAL appends, a config change during
        // the downtime, and a re-walk of the final chunk can miss one lapse - two independent writes cannot close
        // both directions, only a shared batch could.
        _receiptRetention.OnPruningProgress(_blocksReclaimCursor, _sliceCleanupCursor);

        // One batch, and a boundary write carries the cursors with it: a boundary that lands without its
        // cursor reads an unreclaimed backlog as "already level" on the next load, forever.
        bool boundaryDirty = _blocksDeletePointer != _lastSavedBlocksDeletePointer;
        bool cursorDirty = boundaryDirty || _blocksReclaimCursor != _lastSavedBlocksReclaimCursor;
        bool cleanupDirty = _sliceCleanupCursor != _lastSavedSliceCleanupCursor;
        bool balsDirty = boundaryDirty || _balsDeletePointer != _lastSavedBalsDeletePointer;
        if (!cursorDirty && !cleanupDirty && !balsDirty)
        {
            return;
        }

        ulong cursorToSave = _blocksReclaimCursor;
        ulong boundaryToSave = _blocksDeletePointer;
        ulong cleanupToSave = _sliceCleanupCursor;
        ulong balsToSave = _balsDeletePointer;
        using (IWriteBatch batch = _metadataDb.StartWriteBatch())
        {
            if (cursorDirty) batch.Set(MetadataDbKeys.HistoryPruningReclaimCursor, Rlp.Encode(cursorToSave).Bytes);
            if (boundaryDirty) batch.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(boundaryToSave).Bytes);
            if (cleanupDirty) batch.Set(MetadataDbKeys.HistoryPruningSliceCleanupCursor, Rlp.Encode(cleanupToSave).Bytes);
            if (balsDirty) batch.Set(MetadataDbKeys.BlockAccessListPruningDeletePointer, Rlp.Encode(balsToSave).Bytes);
        }

        // Only after the batch committed: a throwing commit must leave these claiming unsaved.
        if (cursorDirty) _lastSavedBlocksReclaimCursor = cursorToSave;
        if (boundaryDirty) _lastSavedBlocksDeletePointer = boundaryToSave;
        if (cleanupDirty) _lastSavedSliceCleanupCursor = cleanupToSave;
        if (balsDirty) _lastSavedBalsDeletePointer = balsToSave;

        if (_logger.IsDebug && boundaryDirty) _logger.Debug($"Persisting oldest block stored = #{boundaryToSave} to disk.");
        if (_logger.IsDebug && balsDirty) _logger.Debug($"Persisting oldest BAL stored = #{balsToSave} to disk.");
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
