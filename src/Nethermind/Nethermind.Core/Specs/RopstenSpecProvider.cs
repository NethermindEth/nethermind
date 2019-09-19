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

using Nethermind.Core.Specs.Forks;

namespace Nethermind.Core.Specs
{
    public class RopstenSpecProvider : ISpecProvider
    {
        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

        public IReleaseSpec GetSpec(long blockNumber)
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
            
            if (blockNumber < ConstantinopleFixBlockNumber)
            {
                return Constantinople.Instance;
            }
            
            if (blockNumber < IstanbulBlockNumber)
            {
                return ConstantinopleFix.Instance;
            }

            return Istanbul.Instance;
        }
        
        public long? DaoBlockNumber { get; } = null;
        public static long SpuriousDragonBlockNumber { get; } = 10;
        public static long ByzantiumBlockNumber { get; } = 1700000;
        public static long ConstantinopleBlockNumber { get; } = 4230000;
        public static long ConstantinopleFixBlockNumber { get; } = 4939394;
        public static long IstanbulBlockNumber { get; } = 6485846;
        
        public int ChainId => 3;
        
        private RopstenSpecProvider()
        {
        }
        
        public static readonly RopstenSpecProvider Instance = new RopstenSpecProvider();
    }
}