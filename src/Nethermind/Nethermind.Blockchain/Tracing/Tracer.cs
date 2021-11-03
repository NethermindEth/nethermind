//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Blockchain.Tracing
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

        public Keccak Trace(Block block, IBlockTracer blockTracer)
        {
            /* We force process since we want to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */

            blockTracer.StartNewBlockTrace(block);

            try
            {
                _blockProcessor.Process(block, _processingOptions, blockTracer);
            }
            catch (Exception)
            {
                _stateProvider.Reset();
                throw;
            }
            
            blockTracer.EndBlockTrace();

            return _stateProvider.StateRoot;
        }

        public void Accept(ITreeVisitor visitor, Keccak stateRoot)
        {
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            if (stateRoot == null) throw new ArgumentNullException(nameof(stateRoot));

            _stateProvider.Accept(visitor, stateRoot);
        }
    }
}
