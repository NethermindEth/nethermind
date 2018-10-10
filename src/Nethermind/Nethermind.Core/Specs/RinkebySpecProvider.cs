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
using System.Numerics;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs
{
    public class RinkebySpecProvider : ISpecProvider
    {
        public IReleaseSpec CurrentSpec => Byzantium.Instance;

        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

        public IReleaseSpec GetSpec(UInt256 blockNumber)
        {
            // TODO: this is not covered by test at the moment
            if (blockNumber < SpuriousDragonBlockNumber)
            {
                return TangerineWhistle.Instance;
            }
            
            if (blockNumber < ByzantiumBlockNumber)
            {
                return SpuriousDragon.Instance;
            }
            
            return Byzantium.Instance;
        }

        public UInt256? DaoBlockNumber { get; } = null;
        public static UInt256 SpuriousDragonBlockNumber { get; } = 3;
        public static UInt256 ByzantiumBlockNumber { get; } = 1035301;
        
        public int ChainId => 4;

        private RinkebySpecProvider()
        {
        }
        
        public static readonly RinkebySpecProvider Instance = new RinkebySpecProvider();
    }
}