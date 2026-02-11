// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Merge.Plugin.Test;

public class TestBranchProcessorInterceptor(IBranchProcessor baseBlockProcessor, int delayMs) : IBranchProcessor
{
    public int DelayMs { get; set; } = delayMs;
    public Exception? ExceptionToThrow { get; set; }

    public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token)
    {
        if (DelayMs > 0)
        {
            Thread.Sleep(DelayMs);
        }

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return baseBlockProcessor.Process(baseBlock, suggestedBlocks, processingOptions, blockTracer, token);
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing
    {
        add => baseBlockProcessor.BlocksProcessing += value;
        remove => baseBlockProcessor.BlocksProcessing -= value;
    }

    public event EventHandler<BlockEventArgs>? BlockProcessing
    {
        add => baseBlockProcessor.BlockProcessing += value;
        remove => baseBlockProcessor.BlockProcessing -= value;
    }

    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed
    {
        add => baseBlockProcessor.BlockProcessed += value;
        remove => baseBlockProcessor.BlockProcessed -= value;
    }
}
