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
    public class MainNetSpecProvider : ISpecProvider
    {
        public IReleaseSpec GenesisSpec => Frontier.Instance;

        public IReleaseSpec GetSpec(long blockNumber)
        {
            if (blockNumber < HomesteadBlockNumber)
            {
                return Frontier.Instance;
            }

            if (blockNumber < DaoBlockNumber)
            {
                return Homestead.Instance;
            }

            if (blockNumber < TangerineWhistleBlockNumber)
            {
                return Dao.Instance;
            }

            if (blockNumber < SpuriousDragonBlockNumber)
            {
                return TangerineWhistle.Instance;
            }

            if (blockNumber < ByzantiumBlockNumber)
            {
                return SpuriousDragon.Instance;
            }

            if (blockNumber < ConstantinopleFixBlockNumber)
            {
                return Byzantium.Instance;
            }
            
            if (blockNumber < IstanbulBlockNumber)
            {
                return ConstantinopleFix.Instance;
            }

            return Istanbul.Instance;
        }

        public static long HomesteadBlockNumber { get; } = 1150000;
        public long? DaoBlockNumber { get; } = 1920000;
        public static long TangerineWhistleBlockNumber { get; } = 2463000;
        public static long SpuriousDragonBlockNumber { get; } = 2675000;
        public static long ByzantiumBlockNumber { get; } = 4370000;
        public static long ConstantinopleFixBlockNumber { get; } = 7280000;
        public static long IstanbulBlockNumber { get; } = 9069000;

        public int ChainId => 1;

        private MainNetSpecProvider()
        {
        }

        public static MainNetSpecProvider Instance = new MainNetSpecProvider();
    }
}