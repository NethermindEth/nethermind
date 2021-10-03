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

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain.Processing
{
    public interface IBlockProcessingQueue
    {
        /// <summary>
        /// Puts the block directly in the processing queue
        /// (external plugins should rather use <see cref="BlockTree.SuggestBlock"/>)
        /// </summary>
        /// <param name="block">Block to be processed</param>
        /// <param name="processingOptions">
        /// Processing options that block processor and transaction processor will adhere to.
        /// </param>
        void Enqueue(Block block, ProcessingOptions processingOptions);
        
        /// <summary>
        /// Fired when all blocks from the processing queue has been taken.
        /// This is used for example by the block producers to notify them that we are fully synchronised.
        /// </summary>
        event EventHandler ProcessingQueueEmpty;

        /// <summary>
        /// Number of blocks in the processing queue.
        /// </summary>
        int Count { get; }
        
        public bool IsEmpty => Count == 0;
        
    }
}
