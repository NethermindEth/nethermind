// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class HeadersSyncFeed : ActivatedSyncFeed<HeadersSyncBatch?>
    {

        private readonly ILogger _logger;
        private readonly ISyncPeerPool _syncPeerPool;
        protected readonly ISyncReport _syncReport;
        protected readonly IBlockTree _blockTree;
        protected readonly ISyncConfig _syncConfig;

        private readonly object _handlerLock = new();

        private readonly int _headersRequestSize = GethSyncLimits.MaxHeaderFetch;
        protected long _lowestRequestedHeaderNumber;

        protected Hash256 _nextHeaderHash;
        protected UInt256? _nextHeaderDiff;

        protected long _pivotNumber;

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

        private IEnumerator<KeyValuePair<long, HeadersSyncBatch>>? _enumerator;
        private ulong _memoryEstimate;
        private long _headersEstimate;

        protected virtual BlockHeader? LowestInsertedBlockHeader => _blockTree.LowestInsertedHeader;

        protected virtual MeasuredProgress HeadersSyncProgressReport => _syncReport.FastBlocksHeaders;
        protected virtual MeasuredProgress HeadersSyncQueueReport => _syncReport.HeadersInQueue;

        protected virtual long HeadersDestinationNumber => 0;
        protected virtual bool AllHeadersDownloaded => (LowestInsertedBlockHeader?.Number ?? long.MaxValue) == 1;

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
            var enumerator = Interlocked.Exchange(ref _enumerator, null) ?? _dependencies.GetEnumerator();

            long count = 0;
            while (enumerator.MoveNext())
            {
                count += enumerator.Current.Value.Response?.Count ?? 0;
            }

            // Stop gap method to reduce allocations from non-struct enumerator
            // https://github.com/dotnet/runtime/pull/38296
            enumerator.Reset();
            _enumerator = enumerator;

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
            var enumerator = Interlocked.Exchange(ref _enumerator, null) ?? _dependencies.GetEnumerator();

            ulong amount = 0;
            while (enumerator.MoveNext())
            {
                amount += (ulong)enumerator.Current.Value?.ResponseSizeEstimate;
            }

            // Stop gap method to reduce allocations from non-struct enumerator
            // https://github.com/dotnet/runtime/pull/38296
            enumerator.Reset();
            _enumerator = enumerator;

            return amount;
        }

        public HeadersSyncFeed(
            IBlockTree? blockTree,
            ISyncPeerPool? syncPeerPool,
            ISyncConfig? syncConfig,
            ISyncReport? syncReport,
            ILogManager? logManager,
            bool alwaysStartHeaderSync = false)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _logger = logManager?.GetClassLogger<HeadersSyncFeed>() ?? throw new ArgumentNullException(nameof(HeadersSyncFeed));

            if (!_syncConfig.UseGethLimitsInFastBlocks)
            {
                _headersRequestSize = NethermindSyncLimits.MaxHeaderFetch;
            }

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
        }

        protected virtual void ResetPivot()
        {
            _pivotNumber = _syncConfig.PivotNumberParsed;
            _lowestRequestedHeaderNumber = _pivotNumber + 1; // Because we want the pivot to be requested
            _nextHeaderHash = _syncConfig.PivotHashParsed;
            _nextHeaderDiff = _syncConfig.PivotTotalDifficultyParsed;

            // Resume logic
            BlockHeader? lowestInserted = _blockTree.LowestInsertedHeader;
            if (lowestInserted is not null && lowestInserted!.Number < _pivotNumber)
            {
                SetExpectedNextHeaderToParent(lowestInserted);
                _lowestRequestedHeaderNumber = lowestInserted.Number;
            }
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
                                 || MemoryInQueue >= MemoryAllowance.FastBlocksMemory
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
            HeadersSyncProgressReport.Update(_pivotNumber);
            HeadersSyncProgressReport.MarkEnd();
            ClearDependencies(); // there may be some dependencies from wrong branches
            _pending.DisposeItems();
            _pending.Clear(); // there may be pending wrong branches
            _sent.DisposeItems();
            _sent.Clear(); // we my still be waiting for some bad branches
            HeadersSyncQueueReport.Update(0L);
            HeadersSyncQueueReport.MarkEnd();
        }

        private void HandleDependentBatches(CancellationToken cancellationToken)
        {
            long? lowest = LowestInsertedBlockHeader?.Number;
            long processedBatchCount = 0;
            const long maxBatchToProcess = 4;
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
                    batch = BuildNewBatch();
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

        private HeadersSyncBatch BuildNewBatch()
        {
            HeadersSyncBatch batch = new();
            batch.MinNumber = _lowestRequestedHeaderNumber - 1;
            batch.StartNumber = Math.Max(HeadersDestinationNumber, _lowestRequestedHeaderNumber - _headersRequestSize);
            batch.RequestSize = (int)Math.Min(_lowestRequestedHeaderNumber - HeadersDestinationNumber, _headersRequestSize);
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
                        .OrderByDescending(kvp => kvp.Key))
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
                    _pending.Enqueue(batch);
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
            rightFiller.MinNumber = batch.MinNumber;
            return rightFiller;
        }

        private static HeadersSyncBatch BuildLeftFiller(HeadersSyncBatch batch, int leftFillerSize)
        {
            HeadersSyncBatch leftFiller = new();
            leftFiller.StartNumber = batch.StartNumber;
            leftFiller.RequestSize = leftFillerSize;
            leftFiller.MinNumber = batch.MinNumber;
            return leftFiller;
        }

        private static HeadersSyncBatch BuildDependentBatch(HeadersSyncBatch batch, long addedLast, long addedEarliest)
        {
            HeadersSyncBatch dependentBatch = new();
            dependentBatch.StartNumber = addedEarliest;
            int count = (int)(addedLast - addedEarliest + 1);
            dependentBatch.RequestSize = count;
            dependentBatch.MinNumber = batch.MinNumber;
            dependentBatch.Response = batch.Response!
                .Skip((int)(addedEarliest - batch.StartNumber))
                .Take(count).ToPooledList(count);
            dependentBatch.ResponseSourcePeer = batch.ResponseSourcePeer;
            return dependentBatch;
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

                _pending.Enqueue(batch);
                return 0;
            }

            long addedLast = batch.StartNumber - 1;
            long addedEarliest = batch.EndNumber + 1;
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

                        break;
                    }

                    // response needs to be cached until predecessors arrive
                    if (header.Hash != _nextHeaderHash)
                    {
                        // If the header is at the exact block number, but the hash does not match, then its a different branch.
                        // However, if the header hash does match the parent of the LowestInsertedBlockHeader, then its just
                        // `_nextHeaderHash` not updated as the `BlockTree.Insert` has not returned yet.
                        // We just let it go to the dependency graph.
                        if (header.Number == (LowestInsertedBlockHeader?.Number ?? _pivotNumber + 1) - 1 && header.Hash != LowestInsertedBlockHeader?.ParentHash)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch - number {header.Number} was {header.Hash} while expected {_nextHeaderHash}");
                            if (batch.ResponseSourcePeer is not null)
                            {
                                _syncPeerPool.ReportBreachOfProtocol(
                                    batch.ResponseSourcePeer,
                                    DisconnectReason.HeaderBatchOnDifferentBranch,
                                    "headers - different branch");
                            }

                            break;
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

                            break;
                        }

                        if (_dependencies.ContainsKey(header.Number))
                        {
                            _pending.Enqueue(batch);
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
                        break;
                    }
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

                header.TotalDifficulty = _nextHeaderDiff;
                AddBlockResult addBlockResult = InsertHeader(header);
                if (addBlockResult == AddBlockResult.InvalidBlock)
                {
                    if (batch.ResponseSourcePeer is not null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID bad block");
                        _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, DisconnectReason.InvalidHeader, $"invalid header {header.ToString(BlockHeader.Format.Short)}");
                    }

                    break;
                }

                addedEarliest = Math.Min(addedEarliest, header.Number);
                addedLast = Math.Max(addedLast, header.Number);
            }

            int added = (int)(addedLast - addedEarliest + 1);
            int leftFillerSize = (int)(addedEarliest - batch.StartNumber);
            int rightFillerSize = (int)(batch.EndNumber - addedLast);
            if (added + leftFillerSize + rightFillerSize != batch.RequestSize)
            {
                throw new Exception($"Added {added} + left {leftFillerSize} + right {rightFillerSize} != request size {batch.RequestSize} in {batch}");
            }

            added = Math.Max(0, added);

            if (added < batch.RequestSize)
            {
                if (added <= 0)
                {
                    batch.Response?.Dispose();
                    batch.Response = null;
                    _pending.Enqueue(batch);
                }
                else
                {
                    if (leftFillerSize > 0)
                    {
                        HeadersSyncBatch leftFiller = BuildLeftFiller(batch, leftFillerSize);
                        _pending.Enqueue(leftFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {leftFiller}");
                    }

                    if (rightFillerSize > 0)
                    {
                        HeadersSyncBatch rightFiller = BuildRightFiller(batch, rightFillerSize);
                        _pending.Enqueue(rightFiller);
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
                HeadersSyncProgressReport.Update(_pivotNumber - LowestInsertedBlockHeader.Number + 1);
            }

            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {LowestInsertedBlockHeader?.Number} | HANDLED {batch}");

            HeadersSyncQueueReport.Update(HeadersInQueue);
            return added;
        }

        private void MarkDirty()
        {
            Volatile.Write(ref _headersEstimate, -1);
            Volatile.Write(ref _memoryEstimate, ulong.MaxValue);
        }

        private AddBlockResult InsertHeader(BlockHeader header)
        {
            if (header.IsGenesis)
            {
                return AddBlockResult.AlreadyKnown;
            }

            return InsertToBlockTree(header);
        }

        protected virtual AddBlockResult InsertToBlockTree(BlockHeader header)
        {
            AddBlockResult insertOutcome = _blockTree.Insert(header);
            if (insertOutcome == AddBlockResult.Added || insertOutcome == AddBlockResult.AlreadyKnown)
            {
                SetExpectedNextHeaderToParent(header);
            }

            return insertOutcome;
        }

        protected void SetExpectedNextHeaderToParent(BlockHeader header)
        {
            _nextHeaderHash = header.ParentHash!;
            _nextHeaderDiff = (header.TotalDifficulty ?? 0) - header.Difficulty;
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
