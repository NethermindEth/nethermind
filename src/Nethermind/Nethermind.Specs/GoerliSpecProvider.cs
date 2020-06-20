﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class GoerliSpecProvider : ISpecProvider
    {
        public static readonly GoerliSpecProvider Instance = new GoerliSpecProvider();

        private GoerliSpecProvider()
        {
        }

        public IReleaseSpec GenesisSpec => ConstantinopleFix.Instance;

        public IReleaseSpec GetSpec(long blockNumber)
        {
            if (blockNumber < IstanbulBlockNumber)
            {
                return ConstantinopleFix.Instance;
            }
            
            if (blockNumber < BerlinBlockNumber)
            {
                return Istanbul.Instance;
            }

            return Berlin.Instance;
        }

        public long? DaoBlockNumber { get; } = null;
        public static long IstanbulBlockNumber => 0x17D433;
        public static long BerlinBlockNumber => long.MaxValue - 1;
        public int ChainId => 0x5;

        public long[] TransitionBlocks { get; } =
        {
            IstanbulBlockNumber,
            BerlinBlockNumber
        };
    }
}