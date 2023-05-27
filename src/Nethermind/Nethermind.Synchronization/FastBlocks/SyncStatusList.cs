// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;

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
                    case FastBlockStatus.Unknown:
                        if (!_cache.TryGet(currentNumber, out BlockInfo blockInfo))
                        {
                            blockInfo = _blockTree.FindCanonicalBlockInfo(currentNumber);
                            if (blockInfo is not null)
                            {
                                _cache.Set(currentNumber, blockInfo);
                            }
                        }

                        blockInfos[collected] = blockInfo;
                        _statuses[currentNumber] = FastBlockStatus.Sent;
                        collected++;
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
            Interlocked.Increment(ref _queueSize);
            _statuses[blockNumber] = FastBlockStatus.Inserted;
        }

        public void MarkUnknown(long blockNumber)
        {
            _statuses[blockNumber] = FastBlockStatus.Unknown;
        }
    }
}
