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
    public class MainnetSpecProvider : ISpecProvider
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
        public IReleaseSpec GenesisSpec => Frontier.Instance;

        public IReleaseSpec GetSpec(long blockNumber) =>
            blockNumber switch
            {
                < HomesteadBlockNumber => Frontier.Instance,
                < DaoBlockNumberConst => Homestead.Instance,
                < TangerineWhistleBlockNumber => Dao.Instance,
                < SpuriousDragonBlockNumber => TangerineWhistle.Instance,
                < ByzantiumBlockNumber => SpuriousDragon.Instance,
                < ConstantinopleFixBlockNumber => Byzantium.Instance,
                < IstanbulBlockNumber => ConstantinopleFix.Instance,
                < MuirGlacierBlockNumber => Istanbul.Instance,
                < BerlinBlockNumber => MuirGlacier.Instance,
                < LondonBlockNumber => Berlin.Instance,
                < ArrowGlacierBlockNumber => London.Instance,
                < GrayGlacierBlockNumber => ArrowGlacier.Instance,
                < ShanghaiBlockNumber => GrayGlacier.Instance,
                _ => Shanghai.Instance
            };

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
        public const long ArrowGlacierBlockNumber = 13_773_000;
        public const long GrayGlacierBlockNumber = 15_050_000;
        public const long ShanghaiBlockNumber = long.MaxValue - 4;
        public const long CancunBlockNumber = long.MaxValue - 3;
        public const long PragueBlockNumber = long.MaxValue - 2;
        public const long OsakaBlockNumber = long.MaxValue - 1;

        public ulong ChainId => Core.ChainId.Mainnet;

        public long[] TransitionBlocks { get; } =
        {
            HomesteadBlockNumber, DaoBlockNumberConst, TangerineWhistleBlockNumber, SpuriousDragonBlockNumber,
            ByzantiumBlockNumber, ConstantinopleFixBlockNumber, IstanbulBlockNumber, MuirGlacierBlockNumber,
            BerlinBlockNumber, LondonBlockNumber, ArrowGlacierBlockNumber, GrayGlacierBlockNumber, ShanghaiBlockNumber
        };

        private MainnetSpecProvider() { }

        public static readonly MainnetSpecProvider Instance = new();
    }
}
