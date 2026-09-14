// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public interface IBlockProcessingQueue
    {
        /// <summary>
        /// Starts processing blocks added to the queue.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops processing blocks added to the queue, optionally draining the queued blocks first.
        /// </summary>
        Task StopAsync(bool processRemainingBlocks = false);

        /// <summary>
        /// Whether blocks have been taken from the queue recently, within <paramref name="maxProcessingInterval"/> seconds.
        /// </summary>
        bool IsProcessingBlocks(ulong? maxProcessingInterval);

        /// <summary>
        /// Puts the block directly in the processing queue
        /// (external plugins should rather use <see cref="BlockTree.SuggestBlock"/>)
        /// </summary>
        /// <param name="block">Block to be processed</param>
        /// <param name="processingOptions">
        /// Processing options that block processor and transaction processor will adhere to.
        /// </param>
        ValueTask Enqueue(Block block, ProcessingOptions processingOptions);

        /// <summary>
        /// Fired when all blocks from the processing queue has been taken.
        /// This is used for example by the block producers to notify them that we are fully synchronised.
        /// </summary>
        event EventHandler ProcessingQueueEmpty;

        event EventHandler<BlockEventArgs> BlockAdded;
        event EventHandler<BlockRemovedEventArgs> BlockRemoved;

        /// <summary>
        /// Fired when processing of a block failed and the block was marked invalid.
        /// </summary>
        event EventHandler<InvalidBlockEventArgs> InvalidBlock;

        /// <summary>
        /// Fired periodically with statistics of the blocks processed from the queue.
        /// </summary>
        event EventHandler<BlockStatistics> NewProcessingStatistics;

        /// <summary>
        /// Number of blocks in the processing queue.
        /// </summary>
        int Count { get; }

        public bool IsEmpty => Count == 0;

        public class InvalidBlockEventArgs : EventArgs
        {
            public Block InvalidBlock { get; init; }
        }
    }
}
