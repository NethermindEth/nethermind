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

            if (blockNumber < LondonBlockNumber)
            {
                return Berlin.Instance;
            }

            return London.Instance;
        }

        public const long HomesteadBlockNumber = 1_150_000;
        public long? DaoBlockNumber => DaoBlockNumberConst;
        public const long DaoBlockNumberConst = 1_920_000;
        public const long TangerineWhistleBlockNumber = 2_463_000;
        public const long SpuriousDragonBlockNumber = 2_675_000;
        public const long ByzantiumBlockNumber = 4_370_000;
        public const long ConstantinopleFixBlockNumber = 7_280_000;
        public const long IstanbulBlockNumber = 9_069_000;
        public const long MuirGlacierBlockNumber = 9_200_000;
        public const long BerlinBlockNumber = 12_244_000;
        public const long LondonBlockNumber = 12_965_000;
        public const long ShanghaiBlockNumber = long.MaxValue -4;
        public const long CancunBlockNumber = long.MaxValue -3;
        public const long PragueBlockNumber = long.MaxValue -2;
        public const long OsakaBlockNumber = long.MaxValue -1;

        public ulong ChainId => Core.ChainId.Mainnet;

        public long[] TransitionBlocks { get; } =
        {
            HomesteadBlockNumber,
            DaoBlockNumberConst,
            TangerineWhistleBlockNumber,
            SpuriousDragonBlockNumber,
            ByzantiumBlockNumber,
            ConstantinopleFixBlockNumber,
            IstanbulBlockNumber,
            MuirGlacierBlockNumber,
            BerlinBlockNumber,
            LondonBlockNumber
        };

        private MainnetSpecProvider() { }

        public static readonly MainnetSpecProvider Instance = new();
    }
}
