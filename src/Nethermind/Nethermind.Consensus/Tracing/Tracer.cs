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
    public class Tracer : ITracer
    {
        private readonly IWorldStateManager _worldStateManager;
        private readonly IBlockchainProcessor _traceProcessor;
        private readonly IBlockchainProcessor _executeProcessor;
        private readonly ProcessingOptions _processingOptions;

        public Tracer(IWorldStateManager worldStateManager, IBlockchainProcessor traceProcessor, IBlockchainProcessor executeProcessor,
            ProcessingOptions processingOptions = ProcessingOptions.Trace)
        {
            _traceProcessor = traceProcessor;
            _executeProcessor = executeProcessor;
            _worldStateManager = worldStateManager;
            _processingOptions = processingOptions;
        }

        private void Process(Block block, IBlockTracer blockTracer, IBlockchainProcessor processor)
        {
            /* We force process since we want to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */

            blockTracer.StartNewBlockTrace(block);
            var worldStateToUse = _worldStateManager.CreateResettableWorldState(block.Header);
            try
            {
                processor.Process(block, _processingOptions, blockTracer);
            }
            catch (Exception)
            {
                worldStateToUse.Reset();
                throw;
            }

            blockTracer.EndBlockTrace();
        }

        public void Trace(Block block, IBlockTracer tracer) => Process(block, tracer, _traceProcessor);

        public void Execute(Block block, IBlockTracer tracer) => Process(block, tracer, _executeProcessor);

        public void Accept(ITreeVisitor visitor, Hash256 stateRoot)
        {
            ArgumentNullException.ThrowIfNull(visitor);
            ArgumentNullException.ThrowIfNull(stateRoot);

            _worldStateManager.Accept(visitor, stateRoot);
        }
    }
}
