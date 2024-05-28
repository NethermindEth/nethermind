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
        IWorldState stateProvider,
        IBlockchainProcessor traceProcessor,
        IBlockchainProcessor executeProcessor,
        ProcessingOptions processingOptions = ProcessingOptions.Trace)
        : ITracer
    {
        private void Process(Block block, IBlockTracer blockTracer, IBlockchainProcessor processor)
        {
            /* We force process since we want to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */

            blockTracer.StartNewBlockTrace(block);

            try
            {
                processor.Process(block, processingOptions, blockTracer);
            }
            catch (Exception)
            {
                stateProvider.Reset();
                throw;
            }

            blockTracer.EndBlockTrace();
        }

        public void Trace(Block block, IBlockTracer tracer) => Process(block, tracer, traceProcessor);

        public void Execute(Block block, IBlockTracer tracer) => Process(block, tracer, executeProcessor);

        public void Accept(ITreeVisitor visitor, Hash256 stateRoot)
        {
            ArgumentNullException.ThrowIfNull(visitor);
            ArgumentNullException.ThrowIfNull(stateRoot);

            stateProvider.Accept(visitor, stateRoot);
        }
    }
}
