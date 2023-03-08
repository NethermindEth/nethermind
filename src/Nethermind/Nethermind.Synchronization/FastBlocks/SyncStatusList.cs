// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class SyncStatusList
    {
        private long _queueSize;
        private readonly IBlockTree _blockTree;

        public long LowestInsertWithoutGaps
        {
            get => _lowestInsertWithoutGaps;
            private set => _lowestInsertWithoutGaps = value;
        }

        public long QueueSize => _queueSize;

        private readonly ConcurrentHashSet<long> _insertedItems = new();
        private readonly ConcurrentQueue<BlockInfo> _retryItems = new();
        private long _lowestSent;
        private Task _cleanupTask = Task.CompletedTask;
        private long _lowestInsertWithoutGaps;

        public SyncStatusList(IBlockTree blockTree, long pivotNumber, long? lowestInserted)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            LowestInsertWithoutGaps = lowestInserted ?? pivotNumber;
            _lowestSent = LowestInsertWithoutGaps + 1;
        }

        public void GetInfosForBatch(BlockInfo?[] blockInfos)
        {
            for (int collected = 0; collected < blockInfos.Length; collected++)
            {
                if (blockInfos[collected] is not null) continue;

                if (!_retryItems.TryDequeue(out BlockInfo blockInfo))
                {
                    long blockNumber = Interlocked.Decrement(ref _lowestSent);
                    if (blockNumber <= 0) break;
                    blockInfo = _blockTree.FindCanonicalBlockInfo(blockNumber);
                }

                blockInfos[collected] = blockInfo;
            }

            ScheduleClearInsertedItemsWithoutGaps();
        }

        public void MarkInserted(BlockInfo blockInfo)
        {
            long blockNumber = blockInfo.BlockNumber;
            if (blockNumber == _lowestInsertWithoutGaps)
            {
                DecrementLowestInsertedWithoutGaps(blockNumber - 1);
                ScheduleClearInsertedItemsWithoutGaps();
            }
            else
            {
                Interlocked.Increment(ref _queueSize);
                _insertedItems.Add(blockNumber);
            }
        }

        public void MarkRetry(BlockInfo blockInfo)
        {
            _retryItems.Enqueue(blockInfo);
        }

        private void ScheduleClearInsertedItemsWithoutGaps()
        {
            if (_cleanupTask.IsCompleted)
            {
                lock (_insertedItems)
                {
                    if (_cleanupTask.IsCompleted)
                    {
                        _cleanupTask = Task.Run(ClearInsertedItemsWithoutGaps);
                    }
                }
            }
        }

        private void ClearInsertedItemsWithoutGaps()
        {
            long blockNumber = LowestInsertWithoutGaps;
            while (_insertedItems.TryRemove(blockNumber))
            {
                DecrementLowestInsertedWithoutGaps(--blockNumber);
                Interlocked.Decrement(ref _queueSize);
            }
        }

        private void DecrementLowestInsertedWithoutGaps(long expectedBlockNumber)
        {
            if (Interlocked.Decrement(ref _lowestInsertWithoutGaps) != expectedBlockNumber)
            {
                ThrowNotPossibleException();
            }
        }

        [DoesNotReturn]
        private void ThrowNotPossibleException()
        {
            throw new InvalidOperationException($"{nameof(LowestInsertWithoutGaps)} decrement failure. This should not be possible.");
        }
    }
}
