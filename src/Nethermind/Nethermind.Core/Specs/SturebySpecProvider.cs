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

namespace Nethermind.Core.Specs
{
    public class SturebySpecProvider : ISpecProvider
    {
        public IReleaseSpec GenesisSpec => Frontier.Instance;

        public IReleaseSpec GetSpec(long blockNumber)
        {
            if (blockNumber < HomesteadBlockNumber)
            {
                return Frontier.Instance;
            }
            
            if (blockNumber < TangerineWhistleBlockNumber)
            {
                return Homestead.Instance;
            }
            
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
        
        public long? DaoBlockNumber { get; } = null;
        public static long HomesteadBlockNumber { get; } = 10000;
        public static long TangerineWhistleBlockNumber { get; } = 15000;
        public static long SpuriousDragonBlockNumber { get; } = 23000;
        public static long ByzantiumBlockNumber { get; } = 30000;
        public static long ConstantinopleBlockNumber { get; } = 40000;
        
        public int ChainId => 314158;

        private SturebySpecProvider()
        {
        }
        
        public static readonly SturebySpecProvider Instance = new SturebySpecProvider();
    }
}