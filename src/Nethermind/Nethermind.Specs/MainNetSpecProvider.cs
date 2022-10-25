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

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class MainnetSpecProvider : ISpecProvider
    {
        private ForkActivation? _theMergeBlock = null;
        private UInt256? _terminalTotalDifficulty = UInt256.Parse("58750000000000000000000");

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber != null)
                _theMergeBlock = blockNumber;
            if (terminalTotalDifficulty != null)
                _terminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber => _theMergeBlock;
        public UInt256? TerminalTotalDifficulty => _terminalTotalDifficulty;
        public IReleaseSpec GenesisSpec => Frontier.Instance;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
            forkActivation switch
            {
                { BlockNumber: < HomesteadBlockNumber } => Frontier.Instance,
                { BlockNumber: < DaoBlockNumberConst } => Homestead.Instance,
                { BlockNumber: < TangerineWhistleBlockNumber } => Dao.Instance,
                { BlockNumber: < SpuriousDragonBlockNumber } => TangerineWhistle.Instance,
                { BlockNumber: < ByzantiumBlockNumber } => SpuriousDragon.Instance,
                { BlockNumber: < ConstantinopleFixBlockNumber } => Byzantium.Instance,
                { BlockNumber: < IstanbulBlockNumber } => ConstantinopleFix.Instance,
                { BlockNumber: < MuirGlacierBlockNumber } => Istanbul.Instance,
                { BlockNumber: < BerlinBlockNumber } => MuirGlacier.Instance,
                { BlockNumber: < LondonBlockNumber } => Berlin.Instance,
                { BlockNumber: < ArrowGlacierBlockNumber } => London.Instance,
                { BlockNumber: < GrayGlacierBlockNumber } => ArrowGlacier.Instance,
                { Timestamp: null } or { Timestamp: < ShanghaiBlockTimestamp } => GrayGlacier.Instance,
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
        public const ulong ShanghaiBlockTimestamp = ulong.MaxValue - 4;
        public const ulong CancunBlockTimestamp = ulong.MaxValue - 3;
        public const ulong PragueBlockTimestamp = ulong.MaxValue - 2;
        public const ulong OsakaBlockTimestamp = ulong.MaxValue - 1;
        public static ForkActivation ShanghaiActivation = (15_050_000, ShanghaiBlockTimestamp);
        public static ForkActivation CancunActivation = (15_050_000, CancunBlockTimestamp);
        public static ForkActivation PragueActivation = (15_050_000, PragueBlockTimestamp);
        public static ForkActivation OsakaActivation = (15_050_000, OsakaBlockTimestamp);

        public ulong ChainId => Core.ChainId.Mainnet;

        public ForkActivation[] TransitionBlocks { get; } =
        {
            HomesteadBlockNumber, DaoBlockNumberConst, TangerineWhistleBlockNumber, SpuriousDragonBlockNumber,
            ByzantiumBlockNumber, ConstantinopleFixBlockNumber, IstanbulBlockNumber, MuirGlacierBlockNumber,
            BerlinBlockNumber, LondonBlockNumber, ArrowGlacierBlockNumber, GrayGlacierBlockNumber,
            //(GrayGlacierBlockNumber, ShanghaiBlockTimestamp), (GrayGlacierBlockNumber, CancunBlockTimestamp),
            //(GrayGlacierBlockNumber, PragueBlockTimestamp), (GrayGlacierBlockNumber, OsakaBlockTimestamp)
        };

        private MainnetSpecProvider() { }

        public static readonly MainnetSpecProvider Instance = new();
    }
}
