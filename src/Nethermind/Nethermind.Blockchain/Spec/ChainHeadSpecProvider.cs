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
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Spec
{
    public class ChainHeadSpecProvider : IChainHeadSpecProvider
    {
        private readonly ISpecProvider _specProvider;
        private readonly IBlockFinder _blockFinder;

        public ChainHeadSpecProvider(ISpecProvider specProvider, IBlockFinder blockFinder)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        }

        public IReleaseSpec GenesisSpec => _specProvider.GenesisSpec;

        public IReleaseSpec GetSpec(long blockNumber) => _specProvider.GetSpec(blockNumber);

        public long? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong ChainId => _specProvider.ChainId;

        public long[] TransitionBlocks => _specProvider.TransitionBlocks;
        
        public IReleaseSpec GetSpec() => GetSpec(_blockFinder.FindBestSuggestedHeader()?.Number ?? 0);
    }
}
