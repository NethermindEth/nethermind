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
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class CustomSpecProvider : ISpecProvider
    {
        private readonly (long BlockNumber, IReleaseSpec Release)[] _transitions;

        public ulong ChainId { get; }
        public long[] TransitionBlocks { get; }

        public CustomSpecProvider(params (long BlockNumber, IReleaseSpec Release)[] transitions) : this(0, transitions)
        {
        }

        public CustomSpecProvider(ulong chainId, params (long BlockNumber, IReleaseSpec Release)[] transitions)
        {
            ChainId = chainId;
            
            if (transitions.Length == 0)
            {
                throw new ArgumentException($"There must be at least one release specified when instantiating {nameof(CustomSpecProvider)}", $"{nameof(transitions)}");
            }

            _transitions = transitions.OrderBy(r => r.BlockNumber).ToArray();
            TransitionBlocks = _transitions.Select(t => t.BlockNumber).ToArray();

            if (transitions[0].BlockNumber != 0L)
            {
                throw new ArgumentException($"First release specified when instantiating {nameof(CustomSpecProvider)} should be at genesis block (0)", $"{nameof(transitions)}");
            }
        }
        
        public IReleaseSpec GenesisSpec => _transitions.Length == 0 ? null : _transitions[0].Release;
        
        public IReleaseSpec GetSpec(long blockNumber)
        {
            IReleaseSpec spec = _transitions[0].Release;
            for (int i = 1; i < _transitions.Length; i++)
            {
                if (blockNumber >= _transitions[i].BlockNumber)
                {
                    spec = _transitions[i].Release;
                }
                else
                {
                    break;    
                }
            }

            return spec;
        }

        public long? DaoBlockNumber
        {
            get
            {
                (long blockNumber, IReleaseSpec daoRelease) = _transitions.SingleOrDefault(t => t.Release == Dao.Instance);
                return daoRelease != null ? blockNumber : (long?)null;
            }
        }

    }
}
