// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class SyncStatusList
    {
        private const int ParallelExistCheckSize = 1024;
        private long _queueSize;
        private readonly IBlockTree _blockTree;
        private readonly FastBlockStatusList _statuses;
        private readonly LruCache<ulong, BlockInfo> _cache = new(maxCapacity: 64, startCapacity: 64, "blockInfo Cache");
        private ulong _lowestInsertWithoutGaps;
        private readonly ulong _lowerBound;
        private readonly ulong _startingLowest;

        /// <summary>
        /// Gets the sync pointer to persist: every block above it, up to the pivot, is inserted.
        /// </summary>
        /// <remarks>
        /// The internal pointer rests on the highest block not yet inserted, and feeds treat a persisted pointer at or
        /// below the lower bound as a finished download. Once this list descends onto the lower bound, that block is still
        /// missing, so the pointer is reported one above it and a feed cannot finish before the block is inserted.
        /// A pointer the list started from was persisted earlier and is reported unchanged.
        /// </remarks>
        public ulong LowestInsertWithoutGaps
        {
            get
            {
                ulong lowest = Volatile.Read(ref _lowestInsertWithoutGaps);
                return lowest == _lowerBound && lowest != 0 && lowest != _startingLowest ? lowest + 1 : lowest;
            }
        }

        public long QueueSize => _queueSize;

        public SyncStatusList(IBlockTree blockTree, ulong pivotNumber, ulong? lowestInserted, ulong lowerBound)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _statuses = new FastBlockStatusList(pivotNumber + 1);

            _lowestInsertWithoutGaps = _startingLowest = lowestInserted ?? pivotNumber;
            _lowerBound = lowerBound;
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

        public bool IsInserted(ulong blockNumber) => _statuses[blockNumber] == FastBlockStatus.Inserted;

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
