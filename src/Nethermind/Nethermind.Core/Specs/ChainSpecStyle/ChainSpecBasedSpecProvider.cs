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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.Releases;

namespace Nethermind.Core.Specs.ChainSpecStyle
{
    public class ChainSpecBasedSpecProvider : ISpecProvider
    {
        private (long BlockNumber, IReleaseSpec Release)[] _transitions;

        private ChainSpec _chainSpec;

        public ChainSpecBasedSpecProvider(ChainSpec chainSpec)
        {
            _chainSpec = chainSpec ?? throw new ArgumentNullException(nameof(chainSpec));
            BuildTransitions();
        }

        private void BuildTransitions()
        {
            SortedSet<long> transitionBlocks = new SortedSet<long>();
            transitionBlocks.Add(0L);
            
            if (_chainSpec.Ethash != null)
            {
                foreach ((long BlockNumber, _) in _chainSpec.Ethash.BlockRewards)
                {
                    transitionBlocks.Add(BlockNumber);
                }
            }

            var transitionProperties = _chainSpec.Parameters.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("Transition"));

            foreach (PropertyInfo propertyInfo in transitionProperties)
            {
                if(propertyInfo.PropertyType == typeof(long))
                {
                    transitionBlocks.Add((long)propertyInfo.GetValue(_chainSpec.Parameters));
                }
                else if (propertyInfo.PropertyType == typeof(long?))
                {
                    var optionalTransition = (long?)propertyInfo.GetValue(_chainSpec.Parameters);
                    if (optionalTransition != null)
                    {
                        transitionBlocks.Add(optionalTransition.Value);
                    }
                }
            }

            _transitions = new (long BlockNumber, IReleaseSpec Release)[transitionBlocks.Count];
            
            int index = 0;
            foreach (long releaseStartBlock in transitionBlocks)
            {
                ReleaseSpec releaseSpec = new ReleaseSpec();
                releaseSpec.MaxCodeSize = _chainSpec.Parameters.MaxCodeSizeTransition > releaseStartBlock ? long.MaxValue : _chainSpec.Parameters.MaxCodeSize;
                _transitions[index] = (releaseStartBlock, releaseSpec);
                index++;
            }
        }

        public IReleaseSpec GenesisSpec => _transitions.Length == 0 ? null : _transitions[0].Release;

        public IReleaseSpec GetSpec(long blockNumber)
        {
            if (_transitions.Length == 0)
            {
                return null;
            }
            
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
                return daoRelease != null ? blockNumber : (long?) null;
            }
        }

        public long PivotBlockNumber { get; } = 0;
        public Keccak PivotBlockHash { get; } = null;

        public int ChainId => 0;
    }
}