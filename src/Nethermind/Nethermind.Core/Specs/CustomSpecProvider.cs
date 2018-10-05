/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Numerics;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs
{
    public class CustomSpecProvider : ISpecProvider
    {
        private readonly (UInt256 BlockNumber, IReleaseSpec Release)[] _transitions;

        public CustomSpecProvider(params (UInt256 BlockNumber, IReleaseSpec Release)[] transitions)
        {
            if (transitions.Length == 0)
            {
                throw new ArgumentException($"There must be at least one release specified when instantiating {nameof(CustomSpecProvider)}", $"{nameof(transitions)}");
            }

            _transitions = transitions.OrderBy(r => r.BlockNumber).ToArray();

            if (transitions[0].BlockNumber != BigInteger.Zero)
            {
                throw new ArgumentException($"First release specified when instantiating {nameof(CustomSpecProvider)} should be at genesis block (0)", $"{nameof(transitions)}");
            }
        }

        public IReleaseSpec CurrentSpec => _transitions.Last().Release;

        public IReleaseSpec GenesisSpec => _transitions.First().Release;
        
        public IReleaseSpec GetSpec(UInt256 blockNumber)
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

        public UInt256? DaoBlockNumber
        {
            get
            {
                (UInt256 blockNumber, IReleaseSpec daoRelease) = _transitions.SingleOrDefault(t => t.Release == Dao.Instance);
                return daoRelease != null ? blockNumber : (UInt256?)null;
            }
        }

        public int ChainId => 0;
    }
}