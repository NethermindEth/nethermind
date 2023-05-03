// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class SyncStatusList
    {
        private long _queueSize;
        private readonly IBlockTree _blockTree;
        private readonly FastBlockStatusList _statuses;

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
            using ArrayPoolList<(int collected, long currentNumber)> toSent = new(blockInfos.Length);

            int collected = 0;
            long currentNumber = LowestInsertWithoutGaps;
            lock (_statuses)
            {
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
                            toSent.Add((collected, currentNumber));
                            _statuses[currentNumber] = FastBlockStatus.Sent;
                            collected++;
                            break;
                        case FastBlockStatus.Inserted:
                            if (currentNumber == LowestInsertWithoutGaps)
                            {
                                LowestInsertWithoutGaps--;
                                _queueSize--;
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

            for (int index = 0; index < toSent.Count; index++)
            {
                (int collected, long currentNumber) sent = toSent[index];
                blockInfos[sent.collected] = _blockTree.FindCanonicalBlockInfo(sent.currentNumber);
            }
        }

        public void MarkInserted(in long blockNumber)
        {
            lock (_statuses)
            {
                _queueSize++;
                _statuses[blockNumber] = FastBlockStatus.Inserted;
            }
        }

        public void MarkUnknown(in long blockNumber)
        {
            lock (_statuses)
            {
                _statuses[blockNumber] = FastBlockStatus.Unknown;
            }
        }
    }
}
