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
        private readonly IWorldState _stateProvider;
        private readonly IBlockchainProcessor _traceProcessor;
        private readonly IBlockchainProcessor _executeProcessor;
        private readonly ProcessingOptions _executeOptions;
        private readonly ProcessingOptions _traceOptions;

        public Tracer(IWorldState stateProvider, IBlockchainProcessor traceProcessor, IBlockchainProcessor executeProcessor,
            ProcessingOptions executeOptions = ProcessingOptions.Trace, ProcessingOptions traceOptions = ProcessingOptions.Trace)
        {
            _traceProcessor = traceProcessor;
            _executeProcessor = executeProcessor;
            _stateProvider = stateProvider;
            _executeOptions = executeOptions;
            _traceOptions = traceOptions;
        }

        private void Process(Block block, IBlockTracer blockTracer, IBlockchainProcessor processor, ProcessingOptions options)
        {
            /* We force process since we want to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */

            blockTracer.StartNewBlockTrace(block);

            try
            {
                processor.Process(block, options, blockTracer);
            }
            catch (Exception)
            {
                _stateProvider.Reset();
                throw;
            }

            blockTracer.EndBlockTrace();
        }

        public void Trace(Block block, IBlockTracer tracer) => Process(block, tracer, _traceProcessor, _traceOptions);

        public void Execute(Block block, IBlockTracer tracer) => Process(block, tracer, _executeProcessor, _executeOptions);

        public void Accept(ITreeVisitor visitor, Hash256 stateRoot)
        {
            ArgumentNullException.ThrowIfNull(visitor);
            ArgumentNullException.ThrowIfNull(stateRoot);

            _stateProvider.Accept(visitor, stateRoot);
        }
    }
}
