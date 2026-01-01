// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs
{
    public class TestSpecProvider : ISpecProvider
    {
        public TestSpecProvider(IReleaseSpec initialSpecToReturn)
        {
            GenesisSpec = initialSpecToReturn;
            NextForkSpec = initialSpecToReturn;
        }

        public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                MergeBlockNumber = (ForkActivation)blockNumber.Value;
            if (terminalTotalDifficulty is not null)
                TerminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber { get; private set; }

        public ulong TimestampFork { get; set; } = ISpecProvider.TimestampForkNever;
        public UInt256? TerminalTotalDifficulty { get; set; }

        public IReleaseSpec GenesisSpec { get; set; }

        IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation) =>
            forkActivation.BlockNumber == 0 || (ForkOnBlockNumber is not null && forkActivation.BlockNumber < ForkOnBlockNumber.Value)
                ? GenesisSpec
                : NextForkSpec;

        public IReleaseSpec NextForkSpec { get; set; }
        public ulong? ForkOnBlockNumber { get; set; }

        public ulong? DaoBlockNumber { get; set; }
        public ulong? BeaconChainGenesisTimestamp { get; set; }
        public ulong? _networkId;
        public ulong NetworkId { get { return _networkId ?? TestBlockchainIds.NetworkId; } set { _networkId = value; } }

        private ulong? _chainId;
        public ulong ChainId { get { return _chainId ?? TestBlockchainIds.ChainId; } set { _chainId = value; } }

        public ForkActivation[] TransitionActivations { get; set; } = [(ForkActivation)0UL];
        public bool AllowTestChainOverride { get; set; } = true;

        private TestSpecProvider() { }

        public static readonly TestSpecProvider Instance = new();
    }
}
