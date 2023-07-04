// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core.Test
{
    public class FixedForkActivationChainHeadSpecProvider : IChainHeadSpecProvider
    {
        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            _specProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
        }

        public ForkActivation? MergeBlockNumber => _specProvider.MergeBlockNumber;
        public ulong TimestampFork => _specProvider.TimestampFork;
        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;
        private readonly ISpecProvider _specProvider;
        private readonly long _fixedBlock;
        private readonly ulong? _timestamp;

        public FixedForkActivationChainHeadSpecProvider(ISpecProvider specProvider, long fixedBlock = 10_000_000, ulong? timestamp = null)
        {
            _specProvider = specProvider;
            _fixedBlock = fixedBlock;
            _timestamp = timestamp;
        }

        public IReleaseSpec GenesisSpec => _specProvider.GenesisSpec;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) => _specProvider.GetSpec(forkActivation);

        public long? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong NetworkId => _specProvider.NetworkId;
        public ulong ChainId => _specProvider.ChainId;

        public ForkActivation[] TransitionActivations => _specProvider.TransitionActivations;

        public IReleaseSpec GetCurrentHeadSpec() => GetSpec((_fixedBlock, _timestamp));
    }
}
