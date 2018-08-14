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

using System.Numerics;

namespace Nethermind.Core.Specs
{
    public class RopstenSpecProvider : ISpecProvider
    {
        public IReleaseSpec CurrentSpec => Byzantium.Instance;

        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

        public IReleaseSpec GetSpec(BigInteger blockNumber)
        {            
            // TODO: this is not covered by test at the moment
            if (blockNumber < 10)
            {
                return TangerineWhistle.Instance;
            }
            
            if (blockNumber < 1700000)
            {
                return SpuriousDragon.Instance;
            }
            
            if (blockNumber < 5000000)
            {
                return Byzantium.Instance;
            }

            return Constantinople.Instance;
        }
        
        public BigInteger? DaoBlockNumber { get; } = null;
        
        public int ChainId => 3;

        private RopstenSpecProvider()
        {
        }
        
        public static RopstenSpecProvider Instance = new RopstenSpecProvider();
    }
}