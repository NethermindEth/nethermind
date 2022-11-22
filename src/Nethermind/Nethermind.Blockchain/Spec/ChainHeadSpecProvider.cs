// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Spec
{
    public class ChainHeadSpecProvider : IChainHeadSpecProvider
    {
        private readonly ISpecProvider _specProvider;
        private readonly IBlockFinder _blockFinder;
        private long _lastHeader = -1;
        private IReleaseSpec? _headerSpec = null;
        private readonly object _lock = new();

        public ChainHeadSpecProvider(ISpecProvider specProvider, IBlockFinder blockFinder)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        }

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            _specProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
        }

        public ForkActivation? MergeBlockNumber => _specProvider.MergeBlockNumber;

        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;

        public IReleaseSpec GenesisSpec => _specProvider.GenesisSpec;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) => _specProvider.GetSpec(forkActivation);

        public long? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong ChainId => _specProvider.ChainId;

        public ForkActivation[] TransitionBlocks => _specProvider.TransitionBlocks;

        public IReleaseSpec GetCurrentHeadSpec()
        {
            long headerNumber = _blockFinder.FindBestSuggestedHeader()?.Number ?? 0;

            // we are fine with potential concurrency issue here, that the spec will change
            // between this if and getting actual header spec
            // this is used only in tx pool and this is not a problem there
            if (headerNumber == _lastHeader)
            {
                IReleaseSpec releaseSpec = _headerSpec;
                if (releaseSpec is not null)
                {
                    return releaseSpec;
                }
            }

            // we want to make sure updates to both fields are consistent though
            lock (_lock)
            {
                _lastHeader = headerNumber;
                return _headerSpec = GetSpec(headerNumber);
            }
        }
    }
}
