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
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin
{
    // ToDo BlockTreeFixer?
    public class BeaconBlocksQueue
    {
        private const int SoftMaxRecoveryQueueSizeInTx = 10000;
        private readonly IBlockTree _blockTree;
        private readonly IBlockProcessingQueue _blockProcessingQueue;
        private readonly BlockingCollection<BlockRef> _beaconBlocksQueue = new(new ConcurrentQueue<BlockRef>());
        private bool _terminalPoWReached;
        private int _currentRecoveryQueueSize;
        private ProcessingOptions _processingOptions = ProcessingOptions.BeaconBlocks;


        public BeaconBlocksQueue(
            IPoSSwitcher poSSwitcher,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue)
        {
            _blockTree = blockTree;
            _blockProcessingQueue = blockProcessingQueue;

            poSSwitcher.TerminalPoWBlockReached += TerminalPoWReached;
        }

        public void Enqueue(Block block)
        {
            if (_terminalPoWReached) // ToDo Concurrency?
                _blockProcessingQueue.Enqueue(block, _processingOptions); // ToDo store receipts
            else
            {
                int currentRecoveryQueueSize = Interlocked.Add(ref _currentRecoveryQueueSize, block.Transactions.Length);
                BlockRef blockRef = currentRecoveryQueueSize >= SoftMaxRecoveryQueueSizeInTx ? new BlockRef(block.Hash!, _processingOptions) : new BlockRef(block, _processingOptions);
                _beaconBlocksQueue.Add(blockRef);
            }
        }
        private void TerminalPoWReached(object? sender, EventArgs e)
        {
            foreach (BlockRef blockRef in _beaconBlocksQueue.GetConsumingEnumerable())
            {
                // ToDo resolving blockRef could be more optimal
                if (blockRef.Resolve(_blockTree))
                {
                    _blockProcessingQueue.Enqueue(blockRef.Block!, _processingOptions); // ToDo store receipts
                }
            }
            _terminalPoWReached = true;
        }
    }
}
