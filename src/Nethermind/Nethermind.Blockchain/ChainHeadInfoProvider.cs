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
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.TxPool;

[assembly:InternalsVisibleTo("Nethermind.TxPool.Test")]

namespace Nethermind.Blockchain
{
    public class ChainHeadInfoProvider : IChainHeadInfoProvider
    {
        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IStateReader stateReader)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, new ChainHeadReadOnlyStateProvider(blockTree, stateReader))
        {
        }
        
        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IAccountStateProvider stateProvider)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, stateProvider)
        {
        }

        public ChainHeadInfoProvider(IChainHeadSpecProvider specProvider, IBlockTree blockTree, IAccountStateProvider stateProvider)
        {
            SpecProvider = specProvider;
            AccountStateProvider = stateProvider;
            
            blockTree.BlockAddedToMain += OnHeadChanged;
        }

        public IChainHeadSpecProvider SpecProvider { get; }
        
        public IAccountStateProvider AccountStateProvider { get; }
        
        public long? BlockGasLimit { get; internal set; }
        
        public UInt256 CurrentBaseFee { get; private set; }
        
        public event EventHandler<BlockReplacementEventArgs>? HeadChanged;
        
        private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
        {
            BlockGasLimit = e.Block!.GasLimit;
            CurrentBaseFee = e.Block.Header.BaseFeePerGas;
            HeadChanged?.Invoke(sender, e);
        }
    }
}
