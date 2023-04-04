// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            if (blockNumber is not null)
                _theMergeBlock = (ForkActivation)blockNumber;
            if (terminalTotalDifficulty is not null)
                _terminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber => _theMergeBlock;
        public ulong TimestampFork => ShanghaiBlockTimestamp;
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
                { Timestamp: < CancunBlockTimestamp } => Shanghai.Instance,
                _ => Cancun.Instance
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
        public const ulong GenesisBlockTimestamp = 1_438_269_973;
        public const ulong ShanghaiBlockTimestamp = 1_681_338_455;
        public const ulong CancunBlockTimestamp = ulong.MaxValue - 3;
        public const ulong PragueBlockTimestamp = ulong.MaxValue - 2;
        public const ulong OsakaBlockTimestamp = ulong.MaxValue - 1;
        public static ForkActivation ShanghaiActivation = (15_050_001, ShanghaiBlockTimestamp);
        public static ForkActivation CancunActivation = (15_050_002, CancunBlockTimestamp);
        public static ForkActivation PragueActivation = (15_050_003, PragueBlockTimestamp);
        public static ForkActivation OsakaActivation = (15_050_004, OsakaBlockTimestamp);

        public ulong NetworkId => Core.BlockchainIds.Mainnet;
        public ulong ChainId => NetworkId;

        public ForkActivation[] TransitionActivations { get; } =
        {
            (ForkActivation)HomesteadBlockNumber, (ForkActivation)DaoBlockNumberConst, (ForkActivation)TangerineWhistleBlockNumber, (ForkActivation)SpuriousDragonBlockNumber,
            (ForkActivation)ByzantiumBlockNumber, (ForkActivation)ConstantinopleFixBlockNumber, (ForkActivation)IstanbulBlockNumber, (ForkActivation)MuirGlacierBlockNumber,
            (ForkActivation)BerlinBlockNumber, (ForkActivation)LondonBlockNumber, (ForkActivation)ArrowGlacierBlockNumber, (ForkActivation)GrayGlacierBlockNumber,
            ShanghaiActivation, CancunActivation,
            //PragueActivation, OsakaActivation
        };

        public static readonly MainnetSpecProvider Instance = new();
    }
}
