//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Store;

namespace Nethermind.Blockchain.Tracing
{
    public class Tracer : ITracer
    {
        private readonly IStateProvider _stateProvider;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _blockProcessor;

        public Tracer(
            IBlockTree blockTree,
            IStateProvider stateProvider,
            IBlockchainProcessor blockProcessor)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
        }

        public Keccak Trace(Keccak blockHash, IBlockTracer blockTracer)
        {
            Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.RequireCanonical);
            
            /* We force process since we wan to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */
            
            blockTracer.StartNewBlockTrace(block);
            _blockProcessor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain, blockTracer);
            
            return _stateProvider.StateRoot;
        }
        
        public Keccak Trace(Block block, IBlockTracer blockTracer)
        {
            /* We force process since we wan to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */
            
            blockTracer.StartNewBlockTrace(block);
            _blockProcessor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain, blockTracer);

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