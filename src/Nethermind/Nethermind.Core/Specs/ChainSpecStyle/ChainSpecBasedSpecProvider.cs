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
using Nethermind.Dirichlet.Numerics;

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

            if (_chainSpec.Ethash?.BlockRewards != null)
            {
                foreach ((long blockNumber, _) in _chainSpec.Ethash.BlockRewards)
                {
                    transitionBlocks.Add(blockNumber);
                }
            }

            var forks = _chainSpec.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("BlockNumber"));
            
            var baseTransitions = _chainSpec.Parameters.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("Transition"));

            var ethashTransitions = _chainSpec.Ethash?.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("Transition")) ?? Enumerable.Empty<PropertyInfo>();

            var transitionProperties =
                forks.Union(baseTransitions.Union(ethashTransitions));

            foreach (PropertyInfo propertyInfo in transitionProperties)
            {
                if (propertyInfo.PropertyType == typeof(long))
                {
                    transitionBlocks.Add((long) propertyInfo.GetValue(propertyInfo.DeclaringType == typeof(ChainSpec) ? _chainSpec : propertyInfo.DeclaringType == typeof(EthashParameters) ? (object)_chainSpec.Ethash : _chainSpec.Parameters));
                }
                else if (propertyInfo.PropertyType == typeof(long?))
                {
                    var optionalTransition = (long?) propertyInfo.GetValue(propertyInfo.DeclaringType == typeof(ChainSpec) ? _chainSpec : propertyInfo.DeclaringType == typeof(EthashParameters) ? (object)_chainSpec.Ethash : _chainSpec.Parameters);
                    if (optionalTransition != null)
                    {
                        transitionBlocks.Add(optionalTransition.Value);
                    }
                }
            }

            foreach (KeyValuePair<long,long> bombDelay in _chainSpec.Ethash?.DifficultyBombDelays ?? Enumerable.Empty<KeyValuePair<long,long>>())
            {
                transitionBlocks.Add(bombDelay.Key);
            }

            _transitions = new (long BlockNumber, IReleaseSpec Release)[transitionBlocks.Count];

            int index = 0;
            foreach (long releaseStartBlock in transitionBlocks)
            {
                ReleaseSpec releaseSpec = new ReleaseSpec();
                releaseSpec.MaximumUncleCount = (_chainSpec.AuRa?.MaximumUncleCountTransition ?? long.MaxValue) >= releaseStartBlock ? _chainSpec.AuRa?.MaximumUncleCount ?? 2 : 2; 
                releaseSpec.IsTimeAdjustmentPostOlympic = true; // TODO: this is Duration, review
                releaseSpec.Registrar = _chainSpec.Parameters.Registrar;
                releaseSpec.MaximumExtraDataSize = _chainSpec.Parameters.MaximumExtraDataSize;
                releaseSpec.MinGasLimit = _chainSpec.Parameters.MinGasLimit;
                releaseSpec.GasLimitBoundDivisor = _chainSpec.Parameters.GasLimitBoundDivisor;
                releaseSpec.DifficultyBoundDivisor = _chainSpec.Ethash?.DifficultyBoundDivisor ?? 1;
                releaseSpec.MaxCodeSize = _chainSpec.Parameters.MaxCodeSizeTransition > releaseStartBlock ? long.MaxValue : _chainSpec.Parameters.MaxCodeSize;
                releaseSpec.IsEip2Enabled = (_chainSpec.Ethash?.HomesteadTransition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip7Enabled = (_chainSpec.Ethash?.HomesteadTransition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip100Enabled = (_chainSpec.Ethash?.Eip100bTransition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip140Enabled = (_chainSpec.Parameters.Eip140Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip145Enabled = (_chainSpec.Parameters.Eip145Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip150Enabled = (_chainSpec.Parameters.Eip150Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip152Enabled = (_chainSpec.Parameters.Eip152Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip155Enabled = (_chainSpec.Parameters.Eip155Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip160Enabled = (_chainSpec.Parameters.Eip160Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip158Enabled = (_chainSpec.Parameters.Eip161abcTransition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip170Enabled = _chainSpec.Parameters.MaxCodeSizeTransition <= releaseStartBlock;
                releaseSpec.IsEip196Enabled = (_chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip197Enabled = (_chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip198Enabled = (_chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip211Enabled = (_chainSpec.Parameters.Eip211Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip214Enabled = (_chainSpec.Parameters.Eip214Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip658Enabled = (_chainSpec.Parameters.Eip658Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip649Enabled = (_chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip1014Enabled = (_chainSpec.Parameters.Eip1014Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip1052Enabled = (_chainSpec.Parameters.Eip1052Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip1108Enabled = (_chainSpec.Parameters.Eip1108Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip1234Enabled = (_chainSpec.ConstantinopleBlockNumber ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip1283Enabled = (_chainSpec.Parameters.Eip1283Transition ?? long.MaxValue) <= releaseStartBlock && (_chainSpec.Parameters.Eip1283DisableTransition ?? long.MaxValue) > releaseStartBlock;
                releaseSpec.IsEip1344Enabled = (_chainSpec.Parameters.Eip1344Transition ?? long.MaxValue) <= releaseStartBlock;
                releaseSpec.IsEip1884Enabled = (_chainSpec.Parameters.Eip1884Transition ?? 0) <= releaseStartBlock;
                releaseSpec.IsEip2028Enabled = (_chainSpec.Parameters.Eip2028Transition ?? long.MaxValue) <= releaseStartBlock;
                releaseSpec.IsEip2200Enabled = (_chainSpec.Parameters.Eip2200Transition ?? 0) <= releaseStartBlock;
                
                if (_chainSpec.Ethash != null)
                {
                    foreach (KeyValuePair<long,UInt256> blockReward in _chainSpec.Ethash.BlockRewards ?? Enumerable.Empty<KeyValuePair<long, UInt256>>())
                    {
                        if (blockReward.Key <= releaseStartBlock)
                        {
                            releaseSpec.BlockReward = blockReward.Value;
                        }
                    }
                        
                    foreach (KeyValuePair<long,long> bombDelay in _chainSpec.Ethash.DifficultyBombDelays ?? Enumerable.Empty<KeyValuePair<long, long>>())
                    {
                        if (bombDelay.Key <= releaseStartBlock)
                        {
                            releaseSpec.DifficultyBombDelay += bombDelay.Value;
                        }
                    }
                }

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

        public long? DaoBlockNumber => _chainSpec.DaoForkBlockNumber;

        public int ChainId => _chainSpec.ChainId;
    };
}