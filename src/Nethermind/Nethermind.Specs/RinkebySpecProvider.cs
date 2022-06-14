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
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class RinkebySpecProvider : ISpecProvider
    {
        private long? _theMergeBlock = null;

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber != null)
                _theMergeBlock = blockNumber;
            if (terminalTotalDifficulty != null)
                TerminalTotalDifficulty = terminalTotalDifficulty;
        }

        public long? MergeBlockNumber => _theMergeBlock;
        public UInt256? TerminalTotalDifficulty { get; private set; }
        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

        public IReleaseSpec GetSpec(long blockNumber) =>
            blockNumber switch
            {
                < HomesteadBlockNumber => Frontier.Instance,
                < TangerineWhistleBlockNumber => Homestead.Instance,
                < SpuriousDragonBlockNumber => TangerineWhistle.Instance,
                < ByzantiumBlockNumber => SpuriousDragon.Instance,
                < ConstantinopleBlockNumber => Byzantium.Instance,
                < ConstantinopleFixBlockNumber => Constantinople.Instance,
                < IstanbulBlockNumber => ConstantinopleFix.Instance,
                < BerlinBlockNumber => Istanbul.Instance,
                < LondonBlockNumber => Berlin.Instance,
                _ => London.Instance
            };

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
