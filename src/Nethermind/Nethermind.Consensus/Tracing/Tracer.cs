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
        private readonly IStateProvider _stateProvider;
        private readonly IBlockchainProcessor _blockProcessor;
        private readonly ProcessingOptions _processingOptions;

        public Tracer(IStateProvider stateProvider, IBlockchainProcessor blockProcessor, ProcessingOptions processingOptions = ProcessingOptions.Trace)
        {
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _processingOptions = processingOptions;
        }

        public Block? Trace(Block block, IBlockTracer blockTracer)
        {
            /* We force process since we want to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */

            blockTracer.StartNewBlockTrace(block);

            Block? processedBlock;
            try
            {
                processedBlock = _blockProcessor.Process(block, _processingOptions, blockTracer);
            }
            catch (Exception)
            {
                _stateProvider.Reset();
                throw;
            }

            blockTracer.EndBlockTrace();

            return processedBlock;
        }

        public void Accept(ITreeVisitor visitor, Keccak stateRoot)
        {
            if (visitor is null) throw new ArgumentNullException(nameof(visitor));
            if (stateRoot is null) throw new ArgumentNullException(nameof(stateRoot));

            _stateProvider.Accept(visitor, stateRoot);
        }
    }
}
