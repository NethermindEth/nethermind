// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Spec
{
    public class ChainHeadSpecProvider(ISpecProvider specProvider, IBlockFinder blockFinder) : IChainHeadSpecProvider
    {
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly IBlockFinder _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        private ulong? _lastHeader;
        private IReleaseSpec? _headerSpec;
        private readonly Lock _lock = new();

        public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            _specProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
        }

        public ForkActivation? MergeBlockNumber => _specProvider.MergeBlockNumber;

        public ulong TimestampFork => _specProvider.TimestampFork;

        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;

        public IReleaseSpec GenesisSpec => _specProvider.GenesisSpec;

        IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation) => _specProvider.GetSpec(forkActivation);

        public ulong? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong? BeaconChainGenesisTimestamp => _specProvider.BeaconChainGenesisTimestamp;

        public ulong NetworkId => _specProvider.NetworkId;

        public ulong ChainId => _specProvider.ChainId;

        public ForkActivation[] TransitionActivations => _specProvider.TransitionActivations;

        public IReleaseSpec GetCurrentHeadSpec()
        {
            BlockHeader? header = _blockFinder.FindBestSuggestedHeader();
            ulong headerNumber = header?.Number ?? 0;

            // we are fine with potential concurrency issue here, that the spec will change
            // between this if and getting actual header spec
            // this is used only in tx pool and this is not a problem there
            if (_lastHeader.HasValue && headerNumber == _lastHeader.Value)
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
                return _headerSpec = header is not null
                    ? _specProvider.GetSpec(header)
                    : _specProvider.GetSpec((ForkActivation)headerNumber);
            }
        }
    }
}
