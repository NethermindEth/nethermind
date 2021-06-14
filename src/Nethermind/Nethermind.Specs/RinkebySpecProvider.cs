//  Copyright (c) 2021 Demerzel Solutions Limited
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
    public class RinkebySpecProvider : ISpecProvider
    {
        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

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

            if (blockNumber < ConstantinopleFixBlockNumber)
            {
                return Constantinople.Instance;
            }

            if (blockNumber < IstanbulBlockNumber)
            {
                return ConstantinopleFix.Instance;
            }
            
            if (blockNumber < BerlinBlockNumber)
            {
                return Istanbul.Instance;
            }

            if (blockNumber < LondonBlockNumber)
            {
                return Berlin.Instance;
            }
            
            return London.Instance;
        }

        public long? DaoBlockNumber => null;

        public const long HomesteadBlockNumber  = 1;
        public const long TangerineWhistleBlockNumber  = 2;
        public const long SpuriousDragonBlockNumber  = 3;
        public const long ByzantiumBlockNumber  = 1_035_301;
        public const long ConstantinopleBlockNumber  = 3_660_663;
        public const long ConstantinopleFixBlockNumber  = 4_321_234;
        public const long IstanbulBlockNumber  = 5_435_345;
        public const long BerlinBlockNumber  = 8_290_928;
        public const long LondonBlockNumber = 8_897_988;

        public ulong ChainId => Core.ChainId.Rinkeby;

        public long[] TransitionBlocks { get; } =
        {
            HomesteadBlockNumber,
            TangerineWhistleBlockNumber,
            SpuriousDragonBlockNumber,
            ByzantiumBlockNumber,
            ConstantinopleBlockNumber,
            ConstantinopleFixBlockNumber,
            IstanbulBlockNumber,
            BerlinBlockNumber,
            LondonBlockNumber
        };

        private RinkebySpecProvider() { }

        public static readonly RinkebySpecProvider Instance = new();
    }
}
