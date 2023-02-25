// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class SyncStatusList
    {
        private long _queueSize;
        private readonly IBlockTree _blockTree;
        private readonly FastBlockStatusList _statuses;
        private long _lowestInsertWithoutGaps;

        public long LowestInsertWithoutGaps => _lowestInsertWithoutGaps;
        public long QueueSize => _queueSize;

        public SyncStatusList(IBlockTree blockTree, long pivotNumber, long? lowestInserted)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _statuses = new FastBlockStatusList(pivotNumber + 1);

            _lowestInsertWithoutGaps = lowestInserted ?? pivotNumber;
        }

        public void GetInfosForBatch(BlockInfo?[] blockInfos)
        {
            int collected = 0;
            long currentNumber = _lowestInsertWithoutGaps;
            while (collected < blockInfos.Length && currentNumber != 0)
            {
                if (blockInfos[collected] is not null)
                {
                    collected++;
                    continue;
                }

                switch (_statuses.AtomicRead(currentNumber))
                {
                    case FastBlockStatus.Unknown:
                        blockInfos[collected] = _blockTree.FindCanonicalBlockInfo(currentNumber);
                        _statuses.AtomicWrite(currentNumber, FastBlockStatus.Sent);
                        collected++;
                        break;
                    case FastBlockStatus.Inserted:
                        if (currentNumber == Volatile.Read(ref _lowestInsertWithoutGaps))
                        {
                            Interlocked.CompareExchange(ref _lowestInsertWithoutGaps, currentNumber - 1, currentNumber);
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

        public void MarkInserted(in long blockNumber)
        {
            Interlocked.Increment(ref _queueSize);
            _statuses.AtomicWrite(blockNumber, FastBlockStatus.Inserted);
        }

        public void MarkUnknown(in long blockNumber)
        {
            _statuses.AtomicWrite(blockNumber, FastBlockStatus.Unknown);
        }
    }
}
