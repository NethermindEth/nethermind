// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core.Test
{
    public class FixedBlockChainHeadSpecProvider : IChainHeadSpecProvider
    {
        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            _specProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
        }

        public ForkActivation? MergeBlockNumber => _specProvider.MergeBlockNumber;
        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;
        private readonly ISpecProvider _specProvider;
        private readonly long _fixedBlock;

        public FixedBlockChainHeadSpecProvider(ISpecProvider specProvider, long fixedBlock = 10_000_000)
        {
            _specProvider = specProvider;
            _fixedBlock = fixedBlock;
        }

        public IReleaseSpec GenesisSpec => _specProvider.GenesisSpec;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) => _specProvider.GetSpec(forkActivation);

        public long? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong ChainId => _specProvider.ChainId;

        public ForkActivation[] TransitionBlocks => _specProvider.TransitionBlocks;

        public IReleaseSpec GetCurrentHeadSpec() => GetSpec(_fixedBlock);
    }
}
