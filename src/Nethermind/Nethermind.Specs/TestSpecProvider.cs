// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs
{
    public class TestSpecProvider : IForkAwareSpecProvider
    {
        public TestSpecProvider(IReleaseSpec initialSpecToReturn)
        {
            GenesisSpec = initialSpecToReturn;
            NextForkSpec = initialSpecToReturn;
        }

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                MergeBlockNumber = (ForkActivation)blockNumber;
            if (terminalTotalDifficulty is not null)
                TerminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber { get; private set; }

        public ulong TimestampFork { get; set; } = ISpecProvider.TimestampForkNever;
        public UInt256? TerminalTotalDifficulty { get; set; }

        public IReleaseSpec GenesisSpec { get; set; }

        IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation) => forkActivation.BlockNumber == 0 || forkActivation.BlockNumber < ForkOnBlockNumber ? GenesisSpec : NextForkSpec;

        public IReleaseSpec NextForkSpec { get; set; }
        public long? ForkOnBlockNumber { get; set; }

        public long? DaoBlockNumber { get; set; }
        public ulong? BeaconChainGenesisTimestamp { get; set; }
        public ulong? _networkId;
        public ulong NetworkId { get { return _networkId ?? TestBlockchainIds.NetworkId; } set { _networkId = value; } }

        private ulong? _chainId;
        public ulong ChainId { get { return _chainId ?? TestBlockchainIds.ChainId; } set { _chainId = value; } }

        public ForkActivation[] TransitionActivations { get; set; } = [(ForkActivation)0];
        public bool AllowTestChainOverride { get; set; } = true;


        public IEnumerable<string> AvailableForks => ForkRegistry.All.Keys;

        public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec) => ForkRegistry.All.TryGetValue(forkName, out spec);

        private TestSpecProvider() { }

        public static readonly TestSpecProvider Instance = new();
    }
}
