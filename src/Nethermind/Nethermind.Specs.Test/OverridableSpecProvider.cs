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
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Test
{
    public class OverridableSpecProvider : ISpecProvider
    {
        private readonly ISpecProvider _specProvider;
        private readonly Func<IReleaseSpec, IReleaseSpec> _overrideAction;

        public OverridableSpecProvider(ISpecProvider specProvider, Func<IReleaseSpec, IReleaseSpec> overrideAction)
        {
            _specProvider = specProvider;
            _overrideAction = overrideAction;
        }
        
        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            _specProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
        }

        public long? MergeBlockNumber => _specProvider.MergeBlockNumber;

        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;

        public IReleaseSpec GenesisSpec => _overrideAction(_specProvider.GenesisSpec);

        public IReleaseSpec GetSpec(long blockNumber) => _overrideAction(_specProvider.GetSpec(blockNumber));

        public long? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong ChainId => _specProvider.ChainId;

        public long[] TransitionBlocks => _specProvider.TransitionBlocks;
    }
}
