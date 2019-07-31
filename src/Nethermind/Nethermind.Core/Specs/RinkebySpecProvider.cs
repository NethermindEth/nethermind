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
    public class RinkebySpecProvider : ISpecProvider
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

        public static long SpuriousDragonBlockNumber { get; } = 3;
        public static long ByzantiumBlockNumber { get; } = 1035301;
        public static long ConstantinopleBlockNumber { get; } = 3660663;
        public static long ConstantinopleFixBlockNumber { get; } = 4321234;
        public static long IstanbulBlockNumber { get; } = 10000000;
        
        public int ChainId => 4;
        
        private RinkebySpecProvider()
        {
        }
        
        public static readonly RinkebySpecProvider Instance = new RinkebySpecProvider();
    }
}