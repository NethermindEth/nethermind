// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class SyncStatusList
    {
        private long _queueSize;
        private readonly IBlockTree _blockTree;

        public long LowestInsertWithoutGaps { get; private set; }
        public long QueueSize => _queueSize;

        private readonly ConcurrentHashSet<long> _insertedItems = new();
        private readonly ConcurrentQueue<BlockInfo> _retryItems = new();
        private long _lowestSent;

        public SyncStatusList(IBlockTree blockTree, long pivotNumber, long? lowestInserted)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            LowestInsertWithoutGaps = lowestInserted ?? pivotNumber;
            _lowestSent = LowestInsertWithoutGaps + 1;
        }

        public void GetInfosForBatch(BlockInfo?[] blockInfos)
        {
            int collected = 0;
            while (collected < blockInfos.Length)
            {
                if (blockInfos[collected] is not null)
                {
                    collected++;
                    continue;
                }

                if (!_retryItems.TryDequeue(out BlockInfo blockInfo))
                {
                    long blockNumber = Interlocked.Decrement(ref _lowestSent);

                    if (blockNumber <= 0)
                    {
                        break;
                    }

                    blockInfo = _blockTree.FindCanonicalBlockInfo(blockNumber);
                }

                blockInfos[collected] = blockInfo;
                collected++;
            }
        }

        public void MarkInserted(BlockInfo blockInfo)
        {
            long blockNumber = blockInfo.BlockNumber;
            Interlocked.Increment(ref _queueSize);
            if (blockNumber == LowestInsertWithoutGaps)
            {
                do
                {
                    LowestInsertWithoutGaps--;
                    Interlocked.Decrement(ref _queueSize);
                    blockNumber--;
                } while (_insertedItems.TryRemove(blockNumber));
            }
            else
            {
                _insertedItems.Add(blockNumber);
            }
        }

        public void MarkUnknown(BlockInfo blockInfo)
        {
            _retryItems.Enqueue(blockInfo);
        }
    }
}
