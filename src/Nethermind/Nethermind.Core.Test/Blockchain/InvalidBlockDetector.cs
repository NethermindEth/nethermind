// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Core.Test.Blockchain;

public class InvalidBlockDetector
{
    public event EventHandler<InvalidBlockEventArgs>? OnInvalidBlock;

    private void TriggerOnInvalidBlock(Block invalidBlock)
    {
        OnInvalidBlock?.Invoke(this, new InvalidBlockEventArgs()
        {
            InvalidBlock = invalidBlock
        });
    }

    public class InvalidBlockEventArgs : EventArgs
    {
        public Block InvalidBlock { get; init; } = null!;
    }

    internal class BlockProcessorInterceptor(IBlockProcessor baseBlockProcessor, InvalidBlockDetector invalidBlockDetector) : IBlockProcessor
    {
        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options,
            IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
        {
            try
            {
                return baseBlockProcessor.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
            }
            catch (InvalidBlockException)
            {
                invalidBlockDetector.TriggerOnInvalidBlock(suggestedBlock);
                throw;
            }
        }
    }
}
