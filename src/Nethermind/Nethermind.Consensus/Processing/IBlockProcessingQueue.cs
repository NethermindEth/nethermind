// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
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

        event EventHandler<BlockHashEventArgs> BlockRemoved;

        /// <summary>
        /// Number of blocks in the processing queue.
        /// </summary>
        int Count { get; }

        public bool IsEmpty => Count == 0;
    }
}
