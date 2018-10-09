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

using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs
{
    public class RopstenSpecProvider : ISpecProvider
    {
        public IReleaseSpec CurrentSpec => Byzantium.Instance;

        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

        public IReleaseSpec GetSpec(UInt256 blockNumber)
        {            
            if (blockNumber < SpuriousDragonBlockNumber)
            {
                return TangerineWhistle.Instance;
            }
            
            if (blockNumber < ByzantiumBlockNumber)
            {
                return SpuriousDragon.Instance;
            }
            
            if (blockNumber < ConstantinopleBlockNumber)
            {
                return Byzantium.Instance;
            }

            return Constantinople.Instance;
        }
        
        public UInt256? DaoBlockNumber { get; } = null;
        public static UInt256 SpuriousDragonBlockNumber { get; } = 10;
        public static UInt256 ByzantiumBlockNumber { get; } = 1700000;
        public static UInt256 ConstantinopleBlockNumber { get; } = 4230000;
        
        public int ChainId => 3;

        private RopstenSpecProvider()
        {
        }
        
        public static readonly RopstenSpecProvider Instance = new RopstenSpecProvider();
    }
}