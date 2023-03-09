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
        private readonly ReaderWriterLockSlim _readerWriterLockSlim = new();
        private long _lowestInsertWithoutGaps;

        public long LowestInsertWithoutGaps
        {
            get => _lowestInsertWithoutGaps;
        }

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

            long currentNumber = LowestInsertWithoutGaps;
            while (collected < blockInfos.Length && currentNumber != 0)
            {
                if (blockInfos[collected] is not null)
                {
                    collected++;
                    continue;
                }

                bool sent = false;
                lock (_statuses)
                {
                    switch (_statuses[currentNumber])
                    {
                        case FastBlockStatus.Unknown:
                            _statuses[currentNumber] = FastBlockStatus.Sent;
                            sent = true;
                            break;
                        case FastBlockStatus.Inserted:
                            if (currentNumber == LowestInsertWithoutGaps)
                            {
                                _lowestInsertWithoutGaps--;
                                _queueSize--;
                            }
                            break;
                    }
                }

                if (sent)
                {
                    blockInfos[collected++] = _blockTree.FindCanonicalBlockInfo(currentNumber);
                }

                currentNumber--;
            }
        }

        public void MarkInserted(long blockNumber)
        {
            lock (_statuses)
            {
                _queueSize++;
                _statuses[blockNumber] = FastBlockStatus.Inserted;
            }
        }

        public void MarkUnknown(long blockNumber)
        {
            lock (_statuses)
            {
                _statuses[blockNumber] = FastBlockStatus.Unknown;
            }
        }
    }
}
