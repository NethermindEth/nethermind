//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    internal class FastStatusList
    {
        private readonly IBlockTree _blockTree;

        private FastBlockStatus[] Statuses { get; }
        public long LowestInsertWithoutGaps => _lowestInsertedWithoutGaps;
        public long QueueSize => _queueSize;
        private long _queueSize;
        private long _lowestInsertedWithoutGaps;

        public FastStatusList(IBlockTree blockTree, long pivotNumber)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _lowestInsertedWithoutGaps = pivotNumber;
            Statuses = new FastBlockStatus[pivotNumber + 1];
        }

        public void GetInfosForBatch(BlockInfo[] blockInfos)
        {
            int collected = 0;

            long currentNumber = LowestInsertWithoutGaps;
            lock (Statuses)
            {
                while (collected < blockInfos.Length && currentNumber != 0)
                {
                    if (blockInfos[collected] != null)
                    {
                        collected++;
                        continue;
                    }
                    
                    switch (Statuses[currentNumber])
                    {
                        case FastBlockStatus.Unknown:
                            blockInfos[collected] = _blockTree.FindBlockInfo(currentNumber);
                            Statuses[currentNumber] = FastBlockStatus.Sent;
                            collected++;
                            break;
                        case FastBlockStatus.Inserted:
                            if (currentNumber == LowestInsertWithoutGaps)
                            {
                                _lowestInsertedWithoutGaps--;
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
        }

        public void MarkInserted(in long blockNumber)
        {
            Interlocked.Increment(ref _queueSize);
            lock (Statuses)
            {
                Statuses[blockNumber] = FastBlockStatus.Inserted;
            }
        }

        public void MarkUnknown(in long blockNumber)
        {
            lock (Statuses)
            {
                Statuses[blockNumber] = FastBlockStatus.Unknown;
            }
        }
    }
}