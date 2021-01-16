//  Copyright (c) 2021 Demerzel Solutions Limited
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
    internal class SyncStatusList
    {
        private long _queueSize;
        private readonly IBlockTree _blockTree;
        private FastBlockStatus[] _statuses;
        
        public long LowestInsertWithoutGaps { get; private set; }
        public long QueueSize => _queueSize;

        public SyncStatusList(IBlockTree blockTree, long pivotNumber, long? lowestInserted)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _statuses = new FastBlockStatus[pivotNumber + 1];
            
            LowestInsertWithoutGaps = lowestInserted ?? pivotNumber;
        }

        public void GetInfosForBatch(BlockInfo[] blockInfos)
        {
            int collected = 0;

            long currentNumber = LowestInsertWithoutGaps;
            lock (_statuses)
            {
                while (collected < blockInfos.Length && currentNumber != 0)
                {
                    if (blockInfos[collected] != null)
                    {
                        collected++;
                        continue;
                    }
                    
                    switch (_statuses[currentNumber])
                    {
                        case FastBlockStatus.Unknown:
                            blockInfos[collected] = _blockTree.FindCanonicalBlockInfo(currentNumber);
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
        }

        public void MarkInserted(in long blockNumber)
        {
            Interlocked.Increment(ref _queueSize);
            lock (_statuses)
            {
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
        
        private enum FastBlockStatus : byte
        {
            Unknown = 0,
            Sent = 1,
            Inserted = 2,
        }
    }
}
