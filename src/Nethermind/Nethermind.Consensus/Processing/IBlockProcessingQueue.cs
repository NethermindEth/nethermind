// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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
        /// <summary>
        /// Raised when a queued block has been executed and validated, before it is committed and before
        /// <see cref="BlockRemoved"/>. The result is <see cref="ProcessingResult.Success"/> or
        /// <see cref="ProcessingResult.InclusionListUnsatisfied"/>; every other outcome arrives through
        /// <see cref="BlockRemoved"/> alone.
        /// </summary>
        /// <remarks>
        /// Defaulted so an implementation outside this repository keeps compiling. One that does not raise it leaves
        /// its callers waiting for <see cref="BlockRemoved"/>, which is where the answer came from before.
        /// </remarks>
        event EventHandler<BlockHashEventArgs> BlockExecuted
        {
            add { }
            remove { }
        }

        event EventHandler<BlockRemovedEventArgs> BlockRemoved;

        /// <summary>
        /// Completes once no copy of the block is queued or being processed - at once if none is - so a caller
        /// that learnt the verdict from <see cref="BlockExecuted"/> can wait for the block to become readable
        /// through the chain. It completes only after the last copy's <see cref="BlockRemoved"/> has been raised,
        /// so a caller that resumes can register for the block afresh without an event of the old copy reaching it.
        /// With <paramref name="executedOnly"/> it also completes at once for a block that has not had its verdict
        /// yet: such a block is queued, not committing, and its wait would be as long as its processing.
        /// </summary>
        /// <remarks>
        /// Defaulted to "nothing is queued" so an implementation outside this repository keeps compiling; a caller
        /// then proceeds as it did before this existed, without waiting.
        /// </remarks>
        ValueTask WaitUntilRemovedAsync(Hash256 blockHash, bool executedOnly = false) => ValueTask.CompletedTask;

        /// <summary>
        /// Completes when the copy of the block that has had its verdict leaves the queue, committed or not - at once
        /// when no copy has had one. Unlike <see cref="WaitUntilRemovedAsync"/> it does not wait for further copies of
        /// the same hash queued behind that one: they do not delay its commit, and one behind a backlog would hold the
        /// caller for as long as the backlog takes.
        /// </summary>
        /// <remarks>Defaulted to the executed-only removal wait, which is what callers took before this existed.</remarks>
        ValueTask WaitUntilExecutedCopyRemovedAsync(Hash256 blockHash) => WaitUntilRemovedAsync(blockHash, executedOnly: true);

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
