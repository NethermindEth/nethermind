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

using Nethermind.Core.Specs;

namespace Nethermind.Core.Test
{
    public class FixedBlockChainHeadSpecProvider : IChainHeadSpecProvider
    {
        private readonly ISpecProvider _specProvider;
        private readonly long _fixedBlock;

        public FixedBlockChainHeadSpecProvider(ISpecProvider specProvider, long fixedBlock = 10_000_000)
        {
            _specProvider = specProvider;
            _fixedBlock = fixedBlock;
        }

        public IReleaseSpec GenesisSpec => _specProvider.GenesisSpec;

        public IReleaseSpec GetSpec(long blockNumber)
        {
            return _specProvider.GetSpec(blockNumber);
        }

        public long? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong ChainId => _specProvider.ChainId;

        public long[] TransitionBlocks => _specProvider.TransitionBlocks;
        
        public IReleaseSpec GetSpec() => GetSpec(_fixedBlock);
    }
}
