// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class SyncStatusList
    {
        private const int _parallelExistCheckSize = 1024;
        private long _queueSize;
        private readonly IBlockTree _blockTree;
        private readonly FastBlockStatusList _statuses;
        private readonly LruCache<long, BlockInfo> _cache = new(maxCapacity: 64, startCapacity: 64, "blockInfo Cache");
        private long _lowestInsertWithoutGaps;
        private readonly long _lowerBound;

        public long LowestInsertWithoutGaps
        {
            get => _lowestInsertWithoutGaps;
            private init => _lowestInsertWithoutGaps = value;
        }

        public long QueueSize => _queueSize;

        public SyncStatusList(IBlockTree blockTree, long pivotNumber, long? lowestInserted, long lowerBound)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _statuses = new FastBlockStatusList(pivotNumber + 1);

            LowestInsertWithoutGaps = lowestInserted ?? pivotNumber;
            _lowerBound = lowerBound;
        }

        private void GetInfosForBatch(BlockInfo?[] blockInfos)
        {
            int collected = 0;
            long currentNumber = Volatile.Read(ref _lowestInsertWithoutGaps);
            while (collected < blockInfos.Length && currentNumber != 0 && currentNumber >= _lowerBound)
            {
                if (blockInfos[collected] is not null)
                {
                    collected++;
                    continue;
                }

                if (_statuses.TrySet(currentNumber, FastBlockStatus.Sent, out FastBlockStatus status))
                {
                    if (_cache.TryGet(currentNumber, out BlockInfo blockInfo))
                    {
                        _cache.Delete(currentNumber);
                    }
                    else
                    {
                        blockInfo = _blockTree.FindCanonicalBlockInfo(currentNumber);
                    }

                    blockInfos[collected] = blockInfo;
                    collected++;
                }
                else if (status == FastBlockStatus.Inserted)
                {
                    long currentLowest = Volatile.Read(ref _lowestInsertWithoutGaps);
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
        public bool TryGetInfosForBatch(int batchSize, Func<BlockInfo, bool> blockExist, out BlockInfo?[] infos)
        {
            BlockInfo?[] outputArray = new BlockInfo?[batchSize];
            BlockInfo?[] workingArray = new BlockInfo?[batchSize];

            for (int attempt = 0; attempt < 8; attempt++)
            {
                // Because the last clause of GetInfosForBatch increment the _lowestInsertWithoutGap need to be run
                // sequentially, can't find an easy way to parallelize the checking for block exist part in the check
                // So here we are...
                GetInfosForBatch(workingArray);

                bool hasNonNull = false;
                bool hasInserted = false;
                Parallel.For(0, workingArray.Length, (i) =>
                {
                    if (workingArray[i] is not null)
                    {
                        if (blockExist(workingArray[i]))
                        {
                            MarkInserted(workingArray[i].BlockNumber);
                            hasInserted = true;
                            workingArray[i] = null;
                        }
                        else
                        {
                            hasNonNull = true;
                        }
                    }
                });

                if (hasNonNull || !hasInserted)
                {
                    int slot = 0;
                    for (int i = 0; i < workingArray.Length; i++)
                    {
                        if (workingArray[i] is not null)
                        {
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

                    infos = outputArray;
                    return true;
                }

                // At this point, hasNonNull is false and hasInserted is true, meaning all entry in workingArray
                // already exist. We switch to a bigger array to improve parallelization throughput
                if (workingArray.Length < _parallelExistCheckSize)
                {
                    workingArray = new BlockInfo[_parallelExistCheckSize];
                }
            }

            infos = workingArray;
            return false;
        }

        public void MarkInserted(long blockNumber)
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
