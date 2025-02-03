// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

        event EventHandler<BlockRemovedEventArgs> BlockRemoved;

        /// <summary>
        /// Number of blocks in the processing queue.
        /// </summary>
        int Count { get; }

        public bool IsEmpty => Count == 0;
    }

    public static class BlockProcessingQueueExtensions
    {
        public static void RunOnEmpty(this IBlockProcessingQueue queue, Action action)
        {
            if (queue.IsEmpty)
            {
                action();
            }
            else
            {
                EventHandler handler = null!;
                handler = (_, _) =>
                {
                    action();
                    queue.ProcessingQueueEmpty -= handler!;
                };
                queue.ProcessingQueueEmpty += handler;
            }
        }
    }
}
