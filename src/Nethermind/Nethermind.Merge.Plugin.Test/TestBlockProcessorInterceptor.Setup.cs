// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;

namespace Nethermind.Merge.Plugin.Test;

public class TestBlockProcessorInterceptor : IBlockProcessor
{
    private readonly IBlockProcessor _blockProcessorImplementation;
    public int DelayMs { get; set; }
    public Exception? ExceptionToThrow { get; set; }

    public TestBlockProcessorInterceptor(IBlockProcessor baseBlockProcessor, int delayMs)
    {
        _blockProcessorImplementation = baseBlockProcessor;
        DelayMs = delayMs;
    }

    public Block[] Process(Hash256 newBranchStateRoot, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token)
    {
        if (DelayMs > 0)
        {
            Thread.Sleep(DelayMs);
        }

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return _blockProcessorImplementation.Process(newBranchStateRoot, suggestedBlocks, processingOptions, blockTracer, token);
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing
    {
        add => _blockProcessorImplementation.BlocksProcessing += value;
        remove => _blockProcessorImplementation.BlocksProcessing -= value;
    }

    public event EventHandler<BlockEventArgs>? BlockProcessing
    {
        add => _blockProcessorImplementation.BlockProcessing += value;
        remove => _blockProcessorImplementation.BlockProcessing -= value;
    }

    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed
    {
        add => _blockProcessorImplementation.BlockProcessed += value;
        remove => _blockProcessorImplementation.BlockProcessed -= value;
    }

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed
    {
        add => _blockProcessorImplementation.TransactionProcessed += value;
        remove => _blockProcessorImplementation.TransactionProcessed -= value;
    }
}
