// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class MordenSpecProvider : ISpecProvider
    {
        private ForkActivation? _theMergeBlock = null;

        public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                _theMergeBlock = (ForkActivation)blockNumber.Value;
            if (terminalTotalDifficulty is not null)
                TerminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber => _theMergeBlock;
        public ulong TimestampFork => ISpecProvider.TimestampForkNever;
        public UInt256? TerminalTotalDifficulty { get; private set; }
        public IReleaseSpec GenesisSpec => Frontier.Instance;

        IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation) =>
            forkActivation.BlockNumber switch
            {
                < 494_000UL => Frontier.Instance,
                < 1_885_000UL => Homestead.Instance,
                _ => SpuriousDragon.Instance
            };

        public ulong? DaoBlockNumber => null;
        public ulong? BeaconChainGenesisTimestamp => null;

        public ulong NetworkId => BlockchainIds.Morden;
        public ulong ChainId => NetworkId;
        public ForkActivation[] TransitionActivations { get; } = { (ForkActivation)0UL };
    }
}
