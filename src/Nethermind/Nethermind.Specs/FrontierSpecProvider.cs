// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class FrontierSpecProvider : ISpecProvider
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
        public IReleaseSpec GenesisSpec => Frontier.Instance;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) => Frontier.Instance;

        public long? DaoBlockNumber { get; } = null;

        public ulong NetworkId => Core.BlockchainIds.Mainnet;
        public ulong ChainId => NetworkId;
        public Keccak GenesisHash => KnownHashes.MainnetGenesis;
        public ForkActivation[] TransitionActivations { get; } = { (ForkActivation)0 };

        private FrontierSpecProvider()
        {
        }

        public static FrontierSpecProvider Instance = new();
    }
}
