// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class SyncStatusList
    {
        private long _queueSize;
        private readonly IBlockTree _blockTree;
        private readonly FastBlockStatusList _statuses;
        private readonly LruCache<long, BlockInfo> _cache = new(maxCapacity: 16, startCapacity: 16, "blockInfo Cache");

        public long LowestInsertWithoutGaps { get; private set; }
        public long QueueSize => _queueSize;

        public SyncStatusList(IBlockTree blockTree, long pivotNumber, long? lowestInserted)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _statuses = new FastBlockStatusList(pivotNumber + 1);

            LowestInsertWithoutGaps = lowestInserted ?? pivotNumber;
        }

        public void GetInfosForBatch(BlockInfo?[] blockInfos)
        {
            int collected = 0;
            long currentNumber = LowestInsertWithoutGaps;

            while (collected < blockInfos.Length && currentNumber != 0)
            {
                if (blockInfos[collected] is not null)
                {
                    collected++;
                    continue;
                }

                switch (_statuses[currentNumber])
                {
                    case FastBlockStatus.Pending:
                        if (!_cache.TryGet(currentNumber, out BlockInfo blockInfo))
                        {
                            blockInfo = _blockTree.FindCanonicalBlockInfo(currentNumber);
                            if (blockInfo is not null)
                            {
                                _cache.Set(currentNumber, blockInfo);
                            }
                        }

                        if (_statuses.TryMarkSent(currentNumber))
                        {
                            blockInfos[collected] = blockInfo;
                            collected++;
                        }
                        break;
                    case FastBlockStatus.Inserted:
                        if (currentNumber == LowestInsertWithoutGaps)
                        {
                            LowestInsertWithoutGaps--;
                            Interlocked.Decrement(ref _queueSize);
                        }
                        break;
                    case FastBlockStatus.Sent:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                currentNumber--;
            }
        }

        public void MarkInserted(long blockNumber)
        {
            if (_statuses.TryMarkInserted(blockNumber))
            {
                Interlocked.Increment(ref _queueSize);
            }
        }

        public void MarkPending(long blockNumber)
        {
            _statuses.TryMarkPending(blockNumber);
        }
    }
}
