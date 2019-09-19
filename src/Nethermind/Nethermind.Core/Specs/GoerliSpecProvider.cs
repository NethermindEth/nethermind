﻿/*
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

            return Istanbul.Instance;
        }

        public long? DaoBlockNumber { get; } = null;
        public static long IstanbulBlockNumber { get; } = 0x17D433;

        public int ChainId => 0x5;
    }
}