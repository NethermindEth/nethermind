// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing;

public interface IBranchProcessor
{
    /// <summary>
    /// Processes a group of blocks starting with a state defined by the <paramref name="newBranchStateRoot"/>.
    /// </summary>
    /// <param name="baseBlock">Block where the state the processed branch to be built on top.</param>
    /// <param name="suggestedBlocks">List of blocks to be processed.</param>
    /// <param name="processingOptions">Options to use for processor and transaction processor.</param>
    /// <param name="blockTracer">Block tracer to use. By default either <see cref="NullBlockTracer"/> or <see cref="BlockReceiptsTracer"/></param>
    /// <returns>List of processed blocks.</returns>
    //
    Block[] Process(
        BlockHeader? baseBlock,
        IReadOnlyList<Block> suggestedBlocks,
        ProcessingOptions processingOptions,
        IBlockTracer blockTracer,
        CancellationToken token = default);

    /// <summary>
    /// Fired once a block has been executed and validated against its header, before its state is committed and
    /// the chain is updated. Everything that follows is bookkeeping the block's validity no longer depends on, so
    /// a consumer that only needs the verdict, such as the engine API's newPayload, can answer on this rather than
    /// on <see cref="BlockProcessed"/>.
    /// </summary>
    /// <remarks>
    /// Raised for the last block of a branch only, since an earlier one can still be discarded with the branch, and
    /// with the suggested block - see <see cref="BlockExecutedEventArgs"/> - which is what the processing queue knows
    /// the branch by. The block is not readable
    /// through the chain yet: the block tree marks it processed and moves the head only after this, and a consumer
    /// that needs the committed block waits for the processing queue to remove it. Handlers run synchronously on
    /// the block-processing thread, ahead of the commit: whatever they do is on the block's critical path.
    /// </remarks>
    /// <remarks>
    /// Defaulted so an implementation outside this repository keeps compiling. One that does not raise it leaves
    /// every consumer waiting for <see cref="IBlockProcessingQueue.BlockRemoved"/>, which is where the answer came
    /// from before this event existed.
    /// </remarks>
    event EventHandler<BlockExecutedEventArgs> BlockExecuted
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Fired after a single block has been processed.
    /// </summary>
    event EventHandler<BlockProcessedEventArgs> BlockProcessed;

    /// <summary>
    /// Fired before processing a branch, which may contain multiple blocks.
    /// </summary>
    event EventHandler<BlocksProcessingEventArgs> BlocksProcessing;

    /// <summary>
    /// Fired after a branch-processing attempt completes or fails.
    /// </summary>
    /// <remarks>
    /// This is a completion signal. The event args distinguish the attempted branch from the
    /// number of blocks that actually completed processing.
    /// </remarks>
    event EventHandler<BranchProcessingCompletedEventArgs> BranchProcessingCompleted;

    /// <summary>
    /// Fired when a block is being processed.
    /// </summary>
    event EventHandler<BlockEventArgs> BlockProcessing;
}
