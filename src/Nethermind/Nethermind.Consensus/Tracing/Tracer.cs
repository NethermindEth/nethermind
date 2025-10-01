// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Consensus.Tracing
{
    public class Tracer(
        IStateReader stateReader,
        BlockchainProcessorFacade traceProcessor,
        BlockchainProcessorFacade executeProcessor,
        ProcessingOptions executeOptions = ProcessingOptions.Trace,
        ProcessingOptions traceOptions = ProcessingOptions.Trace)
        : ITracer
    {
        private void Process(Block block, IBlockTracer blockTracer, BlockchainProcessorFacade processor, ProcessingOptions options, string? forkName = null)
        {
            /* We force process since we want to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */

            blockTracer.StartNewBlockTrace(block);
            processor.Process(block, options, blockTracer, forkName: forkName);
            blockTracer.EndBlockTrace();
        }

        public void Trace(Block block, IBlockTracer tracer) => Process(block, tracer, traceProcessor, traceOptions);

        public void Execute(Block block, IBlockTracer tracer, string? forkName = null) => Process(block, tracer, executeProcessor, executeOptions, forkName: forkName);

        public void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot) where TCtx : struct, INodeContext<TCtx>
        {
            ArgumentNullException.ThrowIfNull(visitor);
            ArgumentNullException.ThrowIfNull(stateRoot);

            stateReader.RunTreeVisitor(visitor, stateRoot);
        }
    }
}
