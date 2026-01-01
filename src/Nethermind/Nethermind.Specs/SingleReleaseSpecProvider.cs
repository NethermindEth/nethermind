// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class SingleReleaseSpecProvider : ISpecProvider
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
        public ulong TimestampFork { get; set; } = ISpecProvider.TimestampForkNever;
        public UInt256? TerminalTotalDifficulty { get; set; }
        public ulong NetworkId { get; }
        public ulong ChainId { get; }
        public ForkActivation[] TransitionActivations { get; } = { (ForkActivation)0UL };

        private readonly IReleaseSpec _releaseSpec;

        public SingleReleaseSpecProvider(IReleaseSpec releaseSpec, ulong networkId, ulong chainId)
        {
            NetworkId = networkId;
            ChainId = chainId;
            _releaseSpec = releaseSpec;
            if (_releaseSpec == Dao.Instance)
            {
                DaoBlockNumber = 0UL;
            }
        }

        public IReleaseSpec GenesisSpec => _releaseSpec;

        IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation) => _releaseSpec;

        public ulong? DaoBlockNumber { get; }
        public ulong? BeaconChainGenesisTimestamp { get; }

        public string SealEngine { get; set; } = SealEngineType.Ethash;
    }

    public class TestSingleReleaseSpecProvider(IReleaseSpec releaseSpec)
        : SingleReleaseSpecProvider(releaseSpec, TestBlockchainIds.NetworkId, TestBlockchainIds.ChainId);
}
