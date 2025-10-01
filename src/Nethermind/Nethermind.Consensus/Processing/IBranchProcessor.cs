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
        CancellationToken token = default,
        string? forkName = null);

    /// <summary>
    /// Fired after a block has been processed.
    /// </summary>
    event EventHandler<BlockProcessedEventArgs> BlockProcessed;

    /// <summary>
    /// Fired when a branch is being processed.
    /// </summary>
    event EventHandler<BlocksProcessingEventArgs> BlocksProcessing;

    /// <summary>
    /// Fired when a block is being processed.
    /// </summary>
    event EventHandler<BlockEventArgs> BlockProcessing;
}
