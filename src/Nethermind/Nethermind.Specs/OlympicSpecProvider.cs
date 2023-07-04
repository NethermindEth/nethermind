// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class OlympicSpecProvider : ISpecProvider
    {
        private ForkActivation? _theMergeBlock = null;

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                _theMergeBlock = (ForkActivation)blockNumber;
            if (terminalTotalDifficulty is not null)
                TerminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber => _theMergeBlock;
        public ulong TimestampFork => ISpecProvider.TimestampForkNever;
        public UInt256? TerminalTotalDifficulty { get; private set; }
        public IReleaseSpec GenesisSpec => Olympic.Instance;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) => Olympic.Instance;

        public long? DaoBlockNumber => 0L;

        public ulong NetworkId => Core.BlockchainIds.Olympic;
        public ulong ChainId => NetworkId;
        public ForkActivation[] TransitionActivations { get; } = { (ForkActivation)0 };

        private OlympicSpecProvider() { }

        public static readonly OlympicSpecProvider Instance = new();
    }
}
