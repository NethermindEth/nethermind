// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class RopstenSpecProvider : ISpecProvider
    {
        private ForkActivation? _theMergeBlock;
        private UInt256? _terminalTotalDifficulty = 50000000000000000;

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                _theMergeBlock = (ForkActivation)blockNumber;
            if (terminalTotalDifficulty is not null)
                _terminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber => _theMergeBlock;
        public ulong TimestampFork => ISpecProvider.TimestampForkNever;
        public UInt256? TerminalTotalDifficulty => _terminalTotalDifficulty;
        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
            forkActivation.BlockNumber switch
            {
                < SpuriousDragonBlockNumber => TangerineWhistle.Instance,
                < ByzantiumBlockNumber => SpuriousDragon.Instance,
                < ConstantinopleBlockNumber => Byzantium.Instance,
                < ConstantinopleFixBlockNumber => Constantinople.Instance,
                < IstanbulBlockNumber => ConstantinopleFix.Instance,
                < MuirGlacierBlockNumber => Istanbul.Instance,
                < BerlinBlockNumber => MuirGlacier.Instance,
                < LondonBlockNumber => Berlin.Instance,
                _ => London.Instance
            };

        public long? DaoBlockNumber => null;
        public const long SpuriousDragonBlockNumber = 10;
        public const long ByzantiumBlockNumber = 1_700_000;
        public const long ConstantinopleBlockNumber = 4_230_000;
        public const long ConstantinopleFixBlockNumber = 4_939_394;
        public const long IstanbulBlockNumber = 6_485_846;
        public const long MuirGlacierBlockNumber = 7_117_117;
        public const long BerlinBlockNumber = 9_812_189;
        public const long LondonBlockNumber = 10_499_401;

        public ulong NetworkId => Core.BlockchainIds.Ropsten;
        public ulong ChainId => NetworkId;
        public ForkActivation[] TransitionActivations => new ForkActivation[]
        {
            (ForkActivation)SpuriousDragonBlockNumber,
            (ForkActivation)ByzantiumBlockNumber,
            (ForkActivation)ConstantinopleBlockNumber,
            (ForkActivation)ConstantinopleFixBlockNumber,
            (ForkActivation)IstanbulBlockNumber,
            (ForkActivation)MuirGlacierBlockNumber,
            (ForkActivation)BerlinBlockNumber,
            (ForkActivation)LondonBlockNumber
        };

        public RopstenSpecProvider() { }

        public static readonly RopstenSpecProvider Instance = new();
    }
}
