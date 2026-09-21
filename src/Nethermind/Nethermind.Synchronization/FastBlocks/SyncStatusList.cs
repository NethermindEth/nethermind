// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class SyncStatusList
    {
        private const int ParallelExistCheckSize = 1024;

        /// <summary>How long the insert frontier may sit on one block before that block is requeued.</summary>
        /// <remarks>
        /// Comfortably above the request and stale-request timeouts, so a batch that is merely slow is never
        /// duplicated: only a slot that no response will ever settle reaches this.
        /// </remarks>
        private static readonly TimeSpan DefaultStuckFrontierThreshold = TimeSpan.FromSeconds(30);

        private long _queueSize;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly FastBlockStatusList _statuses;
        private readonly LruCache<ulong, BlockInfo> _cache = new(maxCapacity: 64, startCapacity: 64, "blockInfo Cache");
        private ulong _lowestInsertWithoutGaps;
        private readonly ulong _lowerBound;
        private readonly TimeSpan _stuckFrontierThreshold;
        private ulong _watchedFrontier;
        private long _watchedFrontierSince;

        public ulong LowestInsertWithoutGaps
        {
            get => _lowestInsertWithoutGaps;
            private init => _lowestInsertWithoutGaps = value;
        }

        public long QueueSize => _queueSize;

        public SyncStatusList(IBlockTree blockTree, ulong pivotNumber, ulong? lowestInserted, ulong lowerBound, ILogger logger, TimeSpan? stuckFrontierThreshold = null)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _statuses = new FastBlockStatusList(pivotNumber + 1);

            LowestInsertWithoutGaps = lowestInserted ?? pivotNumber;
            _lowerBound = lowerBound;
            _logger = logger;
            _stuckFrontierThreshold = stuckFrontierThreshold ?? DefaultStuckFrontierThreshold;
            _watchedFrontier = LowestInsertWithoutGaps;
            _watchedFrontierSince = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Requeues the frontier block once it has held the frontier for <see cref="_stuckFrontierThreshold"/>.
        /// </summary>
        /// <remarks>
        /// A block claimed as <see cref="FastBlockStatus.Sent"/> is only ever settled by the response it was
        /// claimed for, so a batch slot that is dropped before it reaches the feed pins the frontier for good:
        /// everything below it still downloads and the feed then idles on a full queue that can never drain.
        /// Handing the block back to the next scan costs at worst a duplicate request.
        /// Only the dispatch loop calls this, so the watch fields need no synchronisation.
        /// </remarks>
        private void RequeueStuckFrontier()
        {
            ulong frontier = Volatile.Read(ref _lowestInsertWithoutGaps);
            if (frontier != _watchedFrontier || _statuses[frontier] != FastBlockStatus.Sent)
            {
                // A frontier no batch holds is not orphaned: the scan that follows either claims it or
                // walks past it, so only an uninterrupted stretch of `Sent` is worth timing.
                _watchedFrontier = frontier;
                _watchedFrontierSince = Stopwatch.GetTimestamp();
                return;
            }

            TimeSpan heldFor = Stopwatch.GetElapsedTime(_watchedFrontierSince);
            if (heldFor < _stuckFrontierThreshold) return;

            _watchedFrontierSince = Stopwatch.GetTimestamp();
            if (_statuses.TrySet(frontier, FastBlockStatus.Pending) && _logger.IsWarn)
            {
                _logger.Warn($"Requeued block {frontier}, which held the fast blocks insert frontier for {heldFor.TotalSeconds:N0}s without a response.");
            }
        }

        private void GetInfosForBatch(Span<BlockInfo?> blockInfos)
        {
            int collected = 0;
            ulong currentNumber = Volatile.Read(ref _lowestInsertWithoutGaps);
            while (collected < blockInfos.Length && currentNumber != 0 && currentNumber >= _lowerBound)
            {
                if (blockInfos[collected] is not null)
                {
                    collected++;
                    continue;
                }

                if (_statuses.TrySet(currentNumber, FastBlockStatus.Sent, out FastBlockStatus status))
                {
                    BlockInfo? blockInfo;
                    if (_cache.TryGet(currentNumber, out BlockInfo cachedInfo))
                    {
                        _cache.Delete(currentNumber);
                        blockInfo = cachedInfo;
                    }
                    else
                    {
                        blockInfo = _blockTree.FindCanonicalBlockInfo(currentNumber);
                    }

                    if (blockInfo is null)
                    {
                        _statuses.TrySet(currentNumber, FastBlockStatus.Pending);
                    }
                    else
                    {
                        blockInfos[collected] = blockInfo;
                        collected++;
                    }
                }
                else if (status == FastBlockStatus.Inserted)
                {
                    ulong currentLowest = Volatile.Read(ref _lowestInsertWithoutGaps);
                    if (currentNumber == currentLowest)
                    {
                        if (Interlocked.CompareExchange(ref _lowestInsertWithoutGaps, currentLowest - 1, currentLowest) == currentLowest)
                        {
                            Interlocked.Decrement(ref _queueSize);
                        }
                    }
                }

                currentNumber--;
            }
        }

        /// <summary>
        /// Try get block infos of size `batchSize`.
        /// </summary>
        /// <param name="batchSize"></param>
        /// <param name="blockExist"></param>
        /// <param name="infos"></param>
        /// <returns></returns>
        public bool TryGetInfosForBatch(int batchSize, IBlockDownloadStrategy blockDownloadStrategy, out BlockInfo?[] infos)
        {
            RequeueStuckFrontier();

            ArrayPoolList<BlockInfo?> workingArray = new(batchSize, batchSize);
            try
            {
                // Need to be a max attempt to update sync progress
                const int maxAttempt = 8;
                for (int attempt = 0; attempt < maxAttempt; attempt++)
                {
                    // Because the last clause of GetInfosForBatch increment the _lowestInsertWithoutGap need to be run
                    // sequentially, can't find an easy way to parallelize the checking for block exist part in the check
                    // So here we are...
                    GetInfosForBatch(workingArray.AsSpan());

                    (bool hasNonNull, bool hasInserted) = ClearExistingBlock();

                    if (hasNonNull || !hasInserted)
                    {
                        CompileOutput(out infos);
                        return true;
                    }

                    // At this point, hasNonNull is false and hasInserted is true, meaning all entry in workingArray
                    // already exist. We switch to a bigger array to improve parallelization throughput
                    if (workingArray.Count < ParallelExistCheckSize)
                    {
                        workingArray.Dispose();
                        workingArray = new ArrayPoolList<BlockInfo?>(ParallelExistCheckSize, ParallelExistCheckSize);
                    }
                }

                infos = [];
                return false;

                (bool, bool) ClearExistingBlock()
                {
                    bool hasNonNull = false;
                    bool hasInserted = false;
                    ParallelUnbalancedWork.For(0, workingArray.Count, (i) =>
                    {
                        if (workingArray[i] is not null)
                        {
                            if (blockDownloadStrategy.ShouldDownloadBlock(workingArray[i]))
                            {
                                hasNonNull = true;
                            }
                            else
                            {
                                MarkInserted(workingArray[i].BlockNumber);
                                hasInserted = true;
                                workingArray[i] = null;
                            }
                        }
                    });
                    return (hasNonNull, hasInserted);
                }

                void CompileOutput(out BlockInfo?[] outputArray)
                {
                    int slot = 0;
                    outputArray = new BlockInfo?[batchSize];
                    for (int i = 0; i < workingArray.Count; i++)
                    {
                        if (workingArray[i] is null) continue;

                        if (slot < outputArray.Length)
                        {
                            outputArray[slot] = workingArray[i];
                            slot++;
                        }
                        else
                        {
                            // Not enough space in output we'll need to put back the block
                            MarkPending(workingArray[i]);
                        }
                    }
                }
            }
            finally
            {
                workingArray.Dispose();
            }
        }

        public void MarkInserted(ulong blockNumber)
        {
            if (_statuses.TrySet(blockNumber, FastBlockStatus.Inserted))
            {
                Interlocked.Increment(ref _queueSize);
            }
        }

        public void MarkPending(BlockInfo blockInfo)
        {
            if (_statuses.TrySet(blockInfo.BlockNumber, FastBlockStatus.Pending))
            {
                _cache.Set(blockInfo.BlockNumber, blockInfo);
            }
        }
    }
}
