// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        private readonly IWorldState _stateProvider;
        private readonly IBlockchainProcessor _traceProcessor;
        private readonly IBlockchainProcessor _executeProcessor;
        private readonly ProcessingOptions _processingOptions;

        public Tracer(IWorldState stateProvider, IBlockchainProcessor traceProcessor, IBlockchainProcessor executeProcessor,
            ProcessingOptions processingOptions = ProcessingOptions.Trace)
        {
            _traceProcessor = traceProcessor;
            _executeProcessor = executeProcessor;
            _stateProvider = stateProvider;
            _processingOptions = processingOptions;
        }

        private void Process(Block block, IBlockTracer blockTracer, IBlockchainProcessor processor)
        {
            /* We force process since we want to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */

            blockTracer.StartNewBlockTrace(block);

            try
            {
                processor.Process(block, _processingOptions, blockTracer);
            }
            catch (Exception)
            {
                _stateProvider.Reset();
                throw;
            }

            blockTracer.EndBlockTrace();
        }

        public void Trace(Block block, IBlockTracer tracer) => Process(block, tracer, _traceProcessor);

        public void Execute(Block block, IBlockTracer tracer) => Process(block, tracer, _executeProcessor);

        public void Accept(ITreeVisitor visitor, Keccak stateRoot)
        {
            if (visitor is null) throw new ArgumentNullException(nameof(visitor));
            if (stateRoot is null) throw new ArgumentNullException(nameof(stateRoot));

            _stateProvider.Accept(visitor, stateRoot);
        }
    }
}
