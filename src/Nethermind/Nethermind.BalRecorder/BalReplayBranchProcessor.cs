// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.BalRecorder;

public class BalReplayBranchProcessor(IBranchProcessor inner, IRecordedBalStore store) : IBranchProcessor
{
    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed
    {
        add => inner.BlockProcessed += value;
        remove => inner.BlockProcessed -= value;
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing
    {
        add => inner.BlocksProcessing += value;
        remove => inner.BlocksProcessing -= value;
    }

    public event EventHandler<BlockEventArgs>? BlockProcessing
    {
        add => inner.BlockProcessing += value;
        remove => inner.BlockProcessing -= value;
    }

    public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token = default)
    {
        if (store.ReplayEnabled)
        {
            foreach (Block block in suggestedBlocks)
            {
                if (block.BlockAccessList is null && block.Hash is not null)
                    block.BlockAccessList = store.Get(block.Number, block.Hash);
            }
        }
        return inner.Process(baseBlock, suggestedBlocks, processingOptions, blockTracer, token);
    }
}
