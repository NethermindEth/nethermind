//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class MainnetSpecProvider : ISpecProvider
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

            if (blockNumber < MuirGlacierBlockNumber)
            {
                return Istanbul.Instance;
            }
            
            if (blockNumber < BerlinBlockNumber)
            {
                return MuirGlacier.Instance;
            }

            return Berlin.Instance;
        }

        public const long HomesteadBlockNumber = 1150000;
        public long? DaoBlockNumber => 1920000;
        public const long TangerineWhistleBlockNumber = 2463000;
        public const long SpuriousDragonBlockNumber = 2675000;
        public const long ByzantiumBlockNumber = 4370000;
        public const long ConstantinopleFixBlockNumber = 7280000;
        public const long IstanbulBlockNumber = 9069000;
        public const long MuirGlacierBlockNumber = 9200000;
        public const long BerlinBlockNumber = long.MaxValue - 1;

        public int ChainId => 1;

        public long[] TransitionBlocks { get; } =
        {
            HomesteadBlockNumber,
            1920000,
            TangerineWhistleBlockNumber,
            SpuriousDragonBlockNumber,
            ByzantiumBlockNumber,
            ConstantinopleFixBlockNumber,
            IstanbulBlockNumber,
            MuirGlacierBlockNumber,
            BerlinBlockNumber
        };

        private MainnetSpecProvider()
        {
        }

        public static MainnetSpecProvider Instance = new MainnetSpecProvider();
    }
}