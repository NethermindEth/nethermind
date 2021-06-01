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
// 

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Blockchain
{
    public class ChainHeadInfoProvider : IChainHeadInfoProvider
    {
        private readonly IBlockTree _blockTree;
        
        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IStateReader stateReader)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, new ChainHeadReadOnlyStateProvider(blockTree, stateReader))
        {
        }
        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IReadOnlyStateProvider stateProvider)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, stateProvider)
        {
        }
        
        public ChainHeadInfoProvider(IChainHeadSpecProvider specProvider, IBlockTree blockTree, IReadOnlyStateProvider stateProvider)
        {
            SpecProvider = specProvider;
            ReadOnlyStateProvider = stateProvider;
            _blockTree = blockTree;
        }

        public IChainHeadSpecProvider SpecProvider { get; }
        public IReadOnlyStateProvider ReadOnlyStateProvider { get; }
        public UInt256 BaseFee => _blockTree.Head?.Header.BaseFeePerGas ?? UInt256.Zero;
        public event EventHandler<BlockReplacementEventArgs> HeadChanged
        {
            add { _blockTree.BlockAddedToMain += value; }
            remove { _blockTree.BlockAddedToMain -= value; }
        }
        
    }
}
