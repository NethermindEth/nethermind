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

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class RopstenSpecProvider : ISpecProvider
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

        public long? DaoBlockNumber { get; } = null;
        public const long SpuriousDragonBlockNumber  = 10;
        public const long ByzantiumBlockNumber  = 1700000;
        public const long ConstantinopleBlockNumber  = 4230000;
        public const long ConstantinopleFixBlockNumber  = 4939394;
        public const long IstanbulBlockNumber  = 6485846;
        public const long MuirGlacierBlockNumber  = 7117117;
        public const long BerlinBlockNumber  = long.MaxValue - 1;

        public int ChainId => 3;
        public long[] TransitionBlocks => new[]
        {
            SpuriousDragonBlockNumber,
            ByzantiumBlockNumber,
            ConstantinopleBlockNumber,
            ConstantinopleFixBlockNumber,
            IstanbulBlockNumber,
            MuirGlacierBlockNumber,
            BerlinBlockNumber
        };

        private RopstenSpecProvider()
        {
        }

        public static readonly RopstenSpecProvider Instance = new RopstenSpecProvider();
    }
}