// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class HeadersSyncFeed : ActivatedSyncFeed<HeadersSyncBatch?>
    {

        private readonly ILogger _logger;
        private readonly ISyncPeerPool _syncPeerPool;
        protected readonly ISyncReport _syncReport;
        protected readonly IBlockTree _blockTree;
        protected readonly ISyncConfig _syncConfig;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly ITotalDifficultyStrategy _totalDifficultyStrategy;
        private FastBlocksAllocationStrategy _approximateAllocationStrategy = new FastBlocksAllocationStrategy(TransferSpeedType.Headers, 0, false);

        private readonly Lock _handlerLock = new();

        private readonly ulong _fastHeadersMemoryBudget;
        protected long _lowestRequestedHeaderNumber;
        protected long _pivotNumber;

        protected record NextHeader(Hash256 Hash256, UInt256? TotalDifficulty);
        protected NextHeader _expectedNextHeader;

        /// <summary>
        /// Requests awaiting to be sent - these are results of partial or invalid responses being queued again
        /// </summary>
        protected readonly ConcurrentQueue<HeadersSyncBatch> _pending = new();

        /// <summary>
        /// Requests sent to peers for which responses have not been received yet
        /// </summary>
        protected readonly ConcurrentHashSet<HeadersSyncBatch> _sent = new();

        /// <summary>
        /// Responses received from peers but waiting in a queue for some other requests to be handled first
        /// </summary>
        private readonly NonBlocking.ConcurrentDictionary<long, HeadersSyncBatch> _dependencies = new();
        // Stop gap method to reduce allocations from non-struct enumerator
        // https://github.com/dotnet/runtime/pull/38296

        /// <summary>
        /// Its a lock to block every processing if needed in order to reset the whole state.
        /// </summary>
        private readonly ReaderWriterLockSlim _resetLock = new();

        private ulong _memoryEstimate;
        private long _headersEstimate;

        protected virtual BlockHeader? LowestInsertedBlockHeader
        {
            get => _blockTree.LowestInsertedHeader;
            set => _blockTree.LowestInsertedHeader = value;
        }

        protected virtual ProgressLogger HeadersSyncProgressLoggerReport => _syncReport.FastBlocksHeaders;

        protected virtual long HeadersDestinationNumber => 0;
        protected virtual bool AllHeadersDownloaded => (LowestInsertedBlockHeader?.Number ?? long.MaxValue) <= 1;

        protected virtual long TotalBlocks => _blockTree.SyncPivot.BlockNumber;

        public override bool IsFinished => AllHeadersDownloaded;
        private bool AnyHeaderDownloaded => LowestInsertedBlockHeader is not null;

        private long HeadersInQueue
        {
            get
            {
                var headersEstimate = Volatile.Read(ref _headersEstimate);
                if (headersEstimate < 0)
                {
                    headersEstimate = CalculateHeadersInQueue();
                    Volatile.Write(ref _headersEstimate, headersEstimate);
                }

                return headersEstimate;
            }
        }

        private long CalculateHeadersInQueue()
        {
            // Reuse the enumerator
            using var enumerator = _dependencies.GetEnumerator();

            long count = 0;
            while (enumerator.MoveNext())
            {
                count += enumerator.Current.Value.Response?.Count ?? 0;
            }

            return count;
        }

        private ulong MemoryInQueue
        {
            get
            {
                var memoryEstimate = Volatile.Read(ref _memoryEstimate);
                if (memoryEstimate == ulong.MaxValue)
                {
                    memoryEstimate = CalculateMemoryInQueue();
                    Volatile.Write(ref _memoryEstimate, memoryEstimate);
                }

                return memoryEstimate;
            }
        }

        private ulong CalculateMemoryInQueue()
        {
            // Reuse the enumerator
            using var enumerator = _dependencies.GetEnumerator();

            ulong amount = 0;
            while (enumerator.MoveNext())
            {
                amount += (ulong)enumerator.Current.Value?.ResponseSizeEstimate;
            }

            return amount;
        }

        public HeadersSyncFeed(
            IBlockTree? blockTree,
            ISyncPeerPool? syncPeerPool,
            ISyncConfig? syncConfig,
            ISyncReport? syncReport,
            IPoSSwitcher? poSSwitcher,
            ILogManager? logManager,
            ITotalDifficultyStrategy? totalDifficultyStrategy = null,
            bool alwaysStartHeaderSync = false)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _logger = logManager?.GetClassLogger<HeadersSyncFeed>() ?? throw new ArgumentNullException(nameof(HeadersSyncFeed));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _totalDifficultyStrategy = totalDifficultyStrategy ?? new CumulativeTotalDifficultyStrategy();
            _fastHeadersMemoryBudget = syncConfig.FastHeadersMemoryBudget;

            if (!_syncConfig.FastSync && !alwaysStartHeaderSync)
            {
                throw new InvalidOperationException("Entered fast headers mode without fast sync enabled in configuration.");
            }
        }

        public override void InitializeFeed()
        {
            _resetLock.EnterWriteLock();
            try
            {
                PostFinishCleanUp();
                ResetPivot();
            }
            finally
            {
                _resetLock.ExitWriteLock();
            }

            base.InitializeFeed();
            HeadersSyncProgressLoggerReport.Reset(_pivotNumber - (LowestInsertedBlockHeader?.Number ?? 0) + 1, TotalBlocks);
        }

        protected virtual void ResetPivot()
        {
            (_pivotNumber, Hash256 nextHeaderHash) = _blockTree.SyncPivot;
            _lowestRequestedHeaderNumber = _pivotNumber + 1; // Because we want the pivot to be requested
            _expectedNextHeader = new NextHeader(nextHeaderHash, TryGetPivotTotalDifficulty(nextHeaderHash));

            // Resume logic
            BlockHeader? lowestInserted = _blockTree.LowestInsertedHeader;
            if (lowestInserted is not null && lowestInserted!.Number < _pivotNumber)
            {
                if (lowestInserted.TotalDifficulty is null)
                {
                    // When the LowestInsertedHeader is set in blockTree initializer, its TD is not set from block info.
                    // So here we explicitly try to fetch it again.
                    lowestInserted = _blockTree.FindHeader(lowestInserted.Number, BlockTreeLookupOptions.RequireCanonical);

                    // In case of some strange corruption, we will have to reset the whole sync.
                    if (lowestInserted!.TotalDifficulty is null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Missing total difficulty on lowest inserted header: {lowestInserted!.ToString(BlockHeader.Format.Short)}. Resetting header sync.");
                        _blockTree.LowestInsertedHeader = null;
                    }
                }

                if (lowestInserted?.TotalDifficulty is not null)
                {
                    SetExpectedNextHeaderToParent(lowestInserted);
                    _lowestRequestedHeaderNumber = lowestInserted.Number;
                }
            }
        }

        private UInt256 TryGetPivotTotalDifficulty(Hash256 headerHash)
        {
            if (_pivotNumber == LongConverter.FromString(_syncConfig.PivotNumber))
                return _syncConfig.PivotTotalDifficultyParsed; // Pivot is the same as in config

            // Got from header
            BlockHeader? pivotHeader = _blockTree.FindHeader(headerHash, BlockTreeLookupOptions.RequireCanonical);
            if (pivotHeader?.TotalDifficulty is not null) return pivotHeader.TotalDifficulty.Value;

            // Probably PoS
            if (_poSSwitcher.FinalTotalDifficulty is not null) return _poSSwitcher.FinalTotalDifficulty.Value;

            throw new InvalidOperationException(
                $"Unable to determine final total difficulty of pivot ({_pivotNumber}, {headerHash})");
        }

        protected override SyncMode ActivationSyncModes { get; }
            = SyncMode.FastHeaders & ~SyncMode.FastBlocks;

        public override bool IsMultiFeed => true;
        public override AllocationContexts Contexts => AllocationContexts.Headers;

        private bool ShouldBuildANewBatch()
        {
            bool destinationHeaderRequested = _lowestRequestedHeaderNumber == HeadersDestinationNumber;

            bool isImmediateSync = !_syncConfig.DownloadHeadersInFastSync;

            bool noBatchesLeft = AllHeadersDownloaded
                                 || destinationHeaderRequested
                                 || MemoryInQueue >= _fastHeadersMemoryBudget
                                 || isImmediateSync && AnyHeaderDownloaded;

            if (noBatchesLeft)
            {
                if ((AllHeadersDownloaded || (isImmediateSync && AnyHeaderDownloaded)) && CurrentState != SyncFeedState.Finished)
                {
                    FinishAndCleanUp();
                }

                return false;
            }

            return true;
        }

        protected virtual void FinishAndCleanUp()
        {
            Finish();
            PostFinishCleanUp();
        }

        protected void ClearDependencies()
        {
            _dependencies.Values.DisposeItems();
            _dependencies.Clear();
            MarkDirty();
        }

        protected virtual void PostFinishCleanUp()
        {
            HeadersSyncProgressLoggerReport.Update(TotalBlocks);
            HeadersSyncProgressLoggerReport.CurrentQueued = 0;
            HeadersSyncProgressLoggerReport.MarkEnd();
            ClearDependencies(); // there may be some dependencies from wrong branches
            _pending.DisposeItems();
            _pending.Clear(); // there may be pending wrong branches
            _sent.DisposeItems();
            _sent.Clear(); // we my still be waiting for some bad branches
        }

        private bool CanHandleDependentBatch()
        {
            long? lowest = LowestInsertedBlockHeader?.Number;
            return lowest.HasValue && _dependencies.ContainsKey(lowest.Value - 1);
        }

        private void HandleDependentBatches(CancellationToken cancellationToken)
        {
            long? lowest = LowestInsertedBlockHeader?.Number;
            long processedBatchCount = 0;
            long maxBatchToProcess = (MemoryInQueue < _fastHeadersMemoryBudget / 2) ? 2 : 4; // Try to keep queue large
            while (lowest.HasValue && processedBatchCount < maxBatchToProcess && _dependencies.TryRemove(lowest.Value - 1, out HeadersSyncBatch dependentBatch))
            {
                using (dependentBatch)
                {
                    MarkDirty();
                    InsertHeaders(dependentBatch);
                    lowest = LowestInsertedBlockHeader?.Number;
                    cancellationToken.ThrowIfCancellationRequested();

                    processedBatchCount++;
                }
            }
        }

        private bool HasDependencyToProcess
        {
            get
            {
                long? lowest = LowestInsertedBlockHeader?.Number;
                return lowest is not null && _dependencies.ContainsKey(lowest.Value - 1);
            }
        }

        public override Task<HeadersSyncBatch?> PrepareRequest(CancellationToken cancellationToken = default)
        {
            _resetLock.EnterReadLock();
            try
            {
                do
                {
                    HandleDependentBatches(cancellationToken);
                } while (_pending.IsEmpty && !ShouldBuildANewBatch() && HasDependencyToProcess);

                if (_pending.TryDequeue(out HeadersSyncBatch? batch))
                {
                    if (_logger.IsTrace) _logger.Trace($"Dequeue batch {batch}");
                    batch!.MarkRetry();
                }
                else if (ShouldBuildANewBatch())
                {
                    // Set the request size depending on the approximate allocation strategy.
                    // NOTE: Cannot await because of the lock.
                    int requestSize =
                        _syncPeerPool.EstimateRequestLimit(RequestType.Headers, _approximateAllocationStrategy, AllocationContexts.Headers, cancellationToken).Result
                        ?? GethSyncLimits.MaxHeaderFetch;

                    batch = ProcessPersistedHeadersOrBuildNewBatch(requestSize, cancellationToken);
                    if (_logger.IsTrace) _logger.Trace($"New batch {batch}");
                }

                if (batch is not null)
                {
                    _sent.Add(batch);
                    if (batch.StartNumber >= (LowestInsertedBlockHeader?.Number ?? 0) - FastBlocksPriorities.ForHeaders)
                    {
                        batch.Prioritized = true;
                    }

                    LogStateOnPrepare();
                }

                return Task.FromResult(batch);
            }
            finally
            {
                _resetLock.ExitReadLock();
            }
        }

        private HeadersSyncBatch? ProcessPersistedHeadersOrBuildNewBatch(int requestSize, CancellationToken cancellationToken)
        {
            HeadersSyncBatch? batch = null;
            do
            {
                batch = BuildNewBatch(requestSize);
                batch = ProcessPersistedPortion(batch);

                if (batch is null)
                {
                    // Return new pending batch first
                    if (_pending.TryDequeue(out batch)) return batch;

                    // If it can process new batch, do it otherwise, this loop will keep filling up the memory
                    // and a lot of the CPU cycle is spent on calculating memory.
                    if (CanHandleDependentBatch()) HandleDependentBatches(cancellationToken);
                }

            } while (batch is null && ShouldBuildANewBatch() && !cancellationToken.IsCancellationRequested);
            return batch;
        }

        private HeadersSyncBatch BuildNewBatch(int requestSize)
        {
            HeadersSyncBatch batch = new();
            batch.StartNumber = Math.Max(HeadersDestinationNumber, _lowestRequestedHeaderNumber - requestSize);
            batch.RequestSize = (int)Math.Min(_lowestRequestedHeaderNumber - HeadersDestinationNumber, requestSize);
            _lowestRequestedHeaderNumber = batch.StartNumber;
            return batch;
        }

        private void LogStateOnPrepare()
        {
            if (_logger.IsDebug) _logger.Debug($"FastHeader LogStateOnPrepare: LOWEST_INSERTED {LowestInsertedBlockHeader?.Number}, LOWEST_REQUESTED {_lowestRequestedHeaderNumber}, DEPENDENCIES {_dependencies.Count}, SENT: {_sent.Count}, PENDING: {_pending.Count}");
            if (_logger.IsTrace)
            {
                lock (_handlerLock)
                {
                    Dictionary<long, string> all = new();
                    StringBuilder builder = new();
                    builder.AppendLine($"SENT {_sent.Count} PENDING {_pending.Count} DEPENDENCIES {_dependencies.Count}");
                    foreach (var headerDependency in _dependencies)
                    {
                        all.TryAdd(headerDependency.Value.EndNumber, $"  DEPENDENCY {headerDependency.Value}");
                    }

                    foreach (var pendingBatch in _pending)
                    {
                        all.TryAdd(pendingBatch.EndNumber, $"  PENDING    {pendingBatch}");
                    }

                    foreach (var sentBatch in _sent)
                    {
                        all.TryAdd(sentBatch.EndNumber, $"  SENT       {sentBatch}");
                    }

                    foreach (KeyValuePair<long, string> keyValuePair in all
                        .OrderByDescending(static kvp => kvp.Key))
                    {
                        builder.AppendLine(keyValuePair.Value);
                    }

                    _logger.Trace($"{builder}");
                }
            }
        }

        public override SyncResponseHandlingResult HandleResponse(HeadersSyncBatch? batch, PeerInfo peer = null)
        {
            if (batch is null)
            {
                if (_logger.IsDebug) _logger.Debug("Received a NULL batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            _resetLock.EnterReadLock();
            try
            {
                if (!_sent.TryRemove(batch))
                {
                    if (_logger.IsDebug) _logger.Debug("Ignoring batch not in sent record");
                    return SyncResponseHandlingResult.Ignored;
                }

                if ((batch.Response?.Count ?? 0) == 0)
                {
                    batch.MarkHandlingStart();
                    if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                    EnqueueBatch(batch);
                    batch.MarkHandlingEnd();
                    return batch.ResponseSourcePeer is null ? SyncResponseHandlingResult.NotAssigned : SyncResponseHandlingResult.NoProgress;
                }

                try
                {
                    if (batch.RequestSize == 0)
                    {
                        return SyncResponseHandlingResult.OK; // 1
                    }

                    lock (_handlerLock)
                    {
                        batch.MarkHandlingStart();
                        int added = InsertHeaders(batch);
                        return added == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;
                    }
                }
                finally
                {
                    batch.MarkHandlingEnd();
                }
            }
            finally
            {
                _resetLock.ExitReadLock();
                batch.Dispose();
            }
        }

        private static HeadersSyncBatch BuildRightFiller(HeadersSyncBatch batch, int rightFillerSize)
        {
            HeadersSyncBatch rightFiller = new();
            rightFiller.StartNumber = batch.EndNumber - rightFillerSize + 1;
            rightFiller.RequestSize = rightFillerSize;
            return rightFiller;
        }

        private static HeadersSyncBatch BuildLeftFiller(HeadersSyncBatch batch, int leftFillerSize)
        {
            HeadersSyncBatch leftFiller = new();
            leftFiller.StartNumber = batch.StartNumber;
            leftFiller.RequestSize = leftFillerSize;
            return leftFiller;
        }

        private static HeadersSyncBatch BuildDependentBatch(HeadersSyncBatch batch, long addedLast, long addedEarliest)
        {
            HeadersSyncBatch dependentBatch = new();
            dependentBatch.StartNumber = addedEarliest;
            int count = (int)(addedLast - addedEarliest + 1);
            dependentBatch.RequestSize = count;
            dependentBatch.Response = batch.Response!
                .Skip((int)(addedEarliest - batch.StartNumber))
                .Take(count).ToPooledList(count);
            dependentBatch.ResponseSourcePeer = batch.ResponseSourcePeer;
            return dependentBatch;
        }

        private void EnqueueBatch(HeadersSyncBatch batch, bool skipPersisted = false)
        {
            HeadersSyncBatch? left = skipPersisted ? batch : ProcessPersistedPortion(batch);
            if (left is not null)
            {
                _pending.Enqueue(batch);
            }
        }

        /// <summary>
        /// Check for portion of header that is already persisted and process them, returning a null batch
        /// if the whole portion is already persisted and does not require download.
        /// If only portion of the batch is persisted, then return a new batch that need to be downloaded.
        /// </summary>
        /// <param name="batch"></param>
        /// <returns></returns>
        private HeadersSyncBatch? ProcessPersistedPortion(HeadersSyncBatch batch)
        {
            // This only check for the last header though, which is fine as headers are so small, the time it take
            // to download one is more or less the same as the whole batch. So many small batch is slower than
            // less large batch.
            BlockHeader? lastHeader = _blockTree.FindHeader(batch.EndNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (lastHeader is null) return batch;

            using ArrayPoolList<BlockHeader> headers = new ArrayPoolList<BlockHeader>(1);
            headers.Add(lastHeader);
            for (long i = batch.EndNumber - 1; i >= batch.StartNumber; i--)
            {
                // Don't worry about fork, `InsertHeaders` will check for fork and retry if it is not on the right fork.
                BlockHeader nextHeader = _blockTree.FindHeader(lastHeader.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, i);
                if (nextHeader is null) break;
                headers.Add(nextHeader);
                lastHeader = nextHeader;
            }

            headers.AsSpan().Reverse();
            int newRequestSize = batch.RequestSize - headers.Count;
            if (headers.Count > 0)
            {
                using HeadersSyncBatch newBatchToProcess = new HeadersSyncBatch();
                newBatchToProcess.StartNumber = lastHeader.Number;
                newBatchToProcess.RequestSize = headers.Count;
                newBatchToProcess.Response = headers;
                if (_logger.IsDebug) _logger.Debug($"Handling header portion {newBatchToProcess.StartNumber} to {newBatchToProcess.EndNumber} with persisted headers.");
                InsertHeaders(newBatchToProcess);
                MarkDirty();
                HeadersSyncProgressLoggerReport.CurrentQueued = HeadersInQueue;
                HeadersSyncProgressLoggerReport.IncrementSkipped(newBatchToProcess.RequestSize);
            }

            if (newRequestSize == 0) return null;

            batch.RequestSize = newRequestSize;
            return batch;
        }

        protected virtual int InsertHeaders(HeadersSyncBatch batch)
        {
            if (batch.Response is null)
            {
                return 0;
            }

            if (batch.Response.Count > batch.RequestSize)
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Peer sent too long response ({batch.Response.Count}) to {batch}");
                if (batch.ResponseSourcePeer is not null)
                {
                    _syncPeerPool.ReportBreachOfProtocol(
                        batch.ResponseSourcePeer,
                        DisconnectReason.HeaderResponseTooLong,
                        $"response too long ({batch.Response.Count})");
                }

                EnqueueBatch(batch);
                return 0;
            }

            using ArrayPoolList<BlockHeader> headersToAdd = new ArrayPoolList<BlockHeader>(batch.Response.Count);
            (Hash256 nextHeaderHash, UInt256? nextHeaderTotalDifficulty) = _expectedNextHeader;

            long addedLast = batch.StartNumber - 1;
            long addedEarliest = batch.EndNumber + 1;
            BlockHeader? lowestInsertedHeader = null;
            int skippedAtTheEnd = 0;
            for (int i = batch.Response.Count - 1; i >= 0; i--)
            {
                BlockHeader? header = batch.Response[i];
                if (header is null)
                {
                    skippedAtTheEnd++;
                    continue;
                }

                if (header.Number != batch.StartNumber + i)
                {
                    if (batch.ResponseSourcePeer is not null)
                    {
                        _syncPeerPool.ReportBreachOfProtocol(
                            batch.ResponseSourcePeer,
                            DisconnectReason.InconsistentHeaderBatch,
                            "inconsistent headers batch");
                    }

                    break;
                }

                bool isFirst = i == batch.Response.Count - 1 - skippedAtTheEnd;
                if (isFirst)
                {
                    if (!ValidateFirstHeader(header)) break;
                }
                else
                {
                    if (header.Hash != batch.Response[i + 1]?.ParentHash)
                    {
                        if (batch.ResponseSourcePeer is not null)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID inconsistent");
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, DisconnectReason.UnexpectedParentHeader, "headers - response not matching request");
                        }

                        break;
                    }
                }

                headersToAdd.Add(header);

                addedEarliest = Math.Min(addedEarliest, header.Number);
                addedLast = Math.Max(addedLast, header.Number);
            }

            UInt256? totalDifficulty = nextHeaderTotalDifficulty;
            foreach (var blockHeader in headersToAdd.AsSpan())
            {
                blockHeader.TotalDifficulty = totalDifficulty;
                totalDifficulty = DetermineParentTotalDifficulty(blockHeader);
            }

            // Remember, the above loop is in revers order, so this need to be reversed again.
            headersToAdd.AsSpan().Reverse();
            if (headersToAdd.Count > 0)
            {
                InsertHeaders(headersToAdd);
                lowestInsertedHeader = headersToAdd[0];
            }

            int added = (int)(addedLast - addedEarliest + 1);
            int leftFillerSize = (int)(addedEarliest - batch.StartNumber);
            int rightFillerSize = (int)(batch.EndNumber - addedLast);
            if (added + leftFillerSize + rightFillerSize != batch.RequestSize)
            {
                throw new Exception($"Added {added} + left {leftFillerSize} + right {rightFillerSize} != request size {batch.RequestSize} in {batch}");
            }

            if (lowestInsertedHeader is not null && lowestInsertedHeader.Number < (LowestInsertedBlockHeader?.Number ?? long.MaxValue))
            {
                LowestInsertedBlockHeader = lowestInsertedHeader;
                SetExpectedNextHeaderToParent(lowestInsertedHeader);
            }

            added = Math.Max(0, added);

            if (added < batch.RequestSize)
            {
                if (added <= 0)
                {
                    batch.Response?.Dispose();
                    batch.Response = null;
                    EnqueueBatch(batch, true);
                }
                else
                {
                    if (leftFillerSize > 0)
                    {
                        HeadersSyncBatch leftFiller = BuildLeftFiller(batch, leftFillerSize);
                        EnqueueBatch(leftFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {leftFiller}");
                    }

                    if (rightFillerSize > 0)
                    {
                        HeadersSyncBatch rightFiller = BuildRightFiller(batch, rightFillerSize);
                        EnqueueBatch(rightFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {rightFiller}");
                    }
                }
            }

            if (added == 0)
            {
                if (batch.ResponseSourcePeer is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"{batch} - reporting no progress");
                    _syncPeerPool.ReportNoSyncProgress(batch.ResponseSourcePeer, AllocationContexts.Headers);
                }
            }

            if (LowestInsertedBlockHeader is not null)
            {
                HeadersSyncProgressLoggerReport.Update(_pivotNumber - LowestInsertedBlockHeader.Number + 1);
            }

            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {LowestInsertedBlockHeader?.Number} | HANDLED {batch}");

            HeadersSyncProgressLoggerReport.CurrentQueued = HeadersInQueue;
            return added;

            // Well, its the last in the batch, but first processed.
            bool ValidateFirstHeader(BlockHeader header)
            {
                BlockHeader lowestInserted = LowestInsertedBlockHeader;
                // response does not carry expected data
                if (header.Number == lowestInserted?.Number && header.Hash != lowestInserted?.Hash)
                {
                    if (batch.ResponseSourcePeer is not null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID hash");
                        _syncPeerPool.ReportBreachOfProtocol(
                            batch.ResponseSourcePeer,
                            DisconnectReason.UnexpectedHeaderHash,
                            "first hash inconsistent with request");
                    }

                    return true;
                }

                // response needs to be cached until predecessors arrive
                if (header.Hash != nextHeaderHash)
                {
                    // If the header is at the exact block number, but the hash does not match, then its a different branch.
                    // However, if the header hash does match the parent of the LowestInsertedBlockHeader, then its just
                    // `_nextHeaderHash` not updated as the `BlockTree.Insert` has not returned yet.
                    // We just let it go to the dependency graph.
                    if (header.Number == (LowestInsertedBlockHeader?.Number ?? _pivotNumber + 1) - 1 && header.Hash != LowestInsertedBlockHeader?.ParentHash)
                    {
                        if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch - number {header.Number} was {header.Hash} while expected {nextHeaderHash}");
                        if (batch.ResponseSourcePeer is not null)
                        {
                            _syncPeerPool.ReportBreachOfProtocol(
                                batch.ResponseSourcePeer,
                                DisconnectReason.HeaderBatchOnDifferentBranch,
                                "headers - different branch");
                        }

                        return false;
                    }

                    if (header.Number == LowestInsertedBlockHeader?.Number)
                    {
                        if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch");
                        if (batch.ResponseSourcePeer is not null)
                        {
                            _syncPeerPool.ReportBreachOfProtocol(
                                batch.ResponseSourcePeer,
                                DisconnectReason.HeaderBatchOnDifferentBranch,
                                "headers - different branch");
                        }

                        return false;
                    }

                    if (_dependencies.ContainsKey(header.Number))
                    {
                        EnqueueBatch(batch, true);
                        throw new InvalidOperationException($"Only one header dependency expected ({batch})");
                    }
                    long lastNumber = -1;
                    for (int j = 0; j < batch.Response.Count; j++)
                    {
                        BlockHeader? current = batch.Response[j];
                        if (current is not null)
                        {
                            if (lastNumber != -1 && lastNumber < current.Number - 1)
                            {
                                //There is a gap in this response,
                                //so we save the whole batch for now,
                                //and let the next PrepareRequest() handle the disconnect
                                addedEarliest = batch.StartNumber;
                                addedLast = batch.EndNumber;
                                break;
                            }
                            addedEarliest = Math.Min(addedEarliest, current.Number);
                            addedLast = Math.Max(addedLast, current.Number);
                            lastNumber = current.Number;
                        }
                    }
                    HeadersSyncBatch dependentBatch = BuildDependentBatch(batch, addedLast, addedEarliest);
                    _dependencies[header.Number] = dependentBatch;
                    MarkDirty();
                    if (_logger.IsDebug) _logger.Debug($"{batch} -> DEPENDENCY {dependentBatch}");
                    // but we cannot do anything with it yet
                    return false;
                }

                return true;
            }
        }

        private void MarkDirty()
        {
            Volatile.Write(ref _headersEstimate, -1);
            Volatile.Write(ref _memoryEstimate, ulong.MaxValue);
        }

        protected virtual void InsertHeaders(IReadOnlyList<BlockHeader> headersToAdd)
        {
            if (headersToAdd.Count == 0) return;
            if (headersToAdd[0].IsGenesis) headersToAdd = headersToAdd.Slice(1);

            _blockTree.BulkInsertHeader(headersToAdd);
        }

        protected void SetExpectedNextHeaderToParent(BlockHeader header)
        {
            _expectedNextHeader = new NextHeader(header.ParentHash, DetermineParentTotalDifficulty(header));
        }

        protected virtual UInt256? DetermineParentTotalDifficulty(BlockHeader header)
        {
            return _totalDifficultyStrategy.ParentTotalDifficulty(header);
        }

        private bool _disposed = false;

        public override void Dispose()
        {
            if (!_disposed)
            {
                _sent.DisposeItems();
                _pending.DisposeItems();
                _dependencies.Values.DisposeItems();
                base.Dispose();
                _disposed = true;
            }
        }
    }
}
