// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class RinkebySpecProvider : ISpecProvider
    {
        private ForkActivation? _theMergeBlock = null;

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                _theMergeBlock = (ForkActivation)blockNumber.Value;
            if (terminalTotalDifficulty is not null)
                TerminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber => _theMergeBlock;
        public ulong TimestampFork => ISpecProvider.TimestampForkNever;
        public UInt256? TerminalTotalDifficulty { get; private set; }
        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
            forkActivation.BlockNumber switch
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

        public const long HomesteadBlockNumber = 1;
        public const long TangerineWhistleBlockNumber = 2;
        public const long SpuriousDragonBlockNumber = 3;
        public const long ByzantiumBlockNumber = 1_035_301;
        public const long ConstantinopleBlockNumber = 3_660_663;
        public const long ConstantinopleFixBlockNumber = 4_321_234;
        public const long IstanbulBlockNumber = 5_435_345;
        public const long BerlinBlockNumber = 8_290_928;
        public const long LondonBlockNumber = 8_897_988;

        public ulong NetworkId => Core.BlockchainIds.Rinkeby;
        public ulong ChainId => NetworkId;

        public ForkActivation[] TransitionActivations { get; } =
        {
            (ForkActivation)HomesteadBlockNumber,
            (ForkActivation)TangerineWhistleBlockNumber,
            (ForkActivation)SpuriousDragonBlockNumber,
            (ForkActivation)ByzantiumBlockNumber,
            (ForkActivation)ConstantinopleBlockNumber,
            (ForkActivation)ConstantinopleFixBlockNumber,
            (ForkActivation)IstanbulBlockNumber,
            (ForkActivation)BerlinBlockNumber,
            (ForkActivation)LondonBlockNumber
        };

        private RinkebySpecProvider() { }

        public static readonly RinkebySpecProvider Instance = new();
    }
}
