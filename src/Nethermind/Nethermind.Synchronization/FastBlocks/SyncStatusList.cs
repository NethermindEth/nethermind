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
        private readonly ConcurrentQueue<long> _retryItems = new();
        private long _lowestSent;

        public SyncStatusList(IBlockTree blockTree, long pivotNumber, long? lowestInserted)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _lowestSent = LowestInsertWithoutGaps = lowestInserted ?? pivotNumber;
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

                if (!_retryItems.TryDequeue(out long blockNumber))
                {
                    blockNumber = Interlocked.Decrement(ref _lowestSent);
                }

                if (blockNumber <= 0)
                {
                    break;
                }

                blockInfos[collected] = _blockTree.FindCanonicalBlockInfo(blockNumber);
                collected++;
            }
        }

        public void MarkInserted(long blockNumber)
        {
            Interlocked.Increment(ref _queueSize);
            if (blockNumber + 1 == LowestInsertWithoutGaps)
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

        public void MarkUnknown(long blockNumber)
        {
            _retryItems.Enqueue(blockNumber);
        }
    }
}
