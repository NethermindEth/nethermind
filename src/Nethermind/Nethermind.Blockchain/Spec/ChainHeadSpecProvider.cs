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
        private CachedSpec? _cache;

        public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null) =>
            _specProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

        public ForkActivation? MergeBlockNumber => _specProvider.MergeBlockNumber;

        public ulong TimestampFork => _specProvider.TimestampFork;

        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;

        public IReleaseSpec GenesisSpec => _specProvider.GenesisSpec;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) => _specProvider.GetSpec(forkActivation);

        public ulong? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong? BeaconChainGenesisTimestamp => _specProvider.BeaconChainGenesisTimestamp;

        public ulong NetworkId => _specProvider.NetworkId;

        public ulong ChainId => _specProvider.ChainId;

        public ForkActivation[] TransitionActivations => _specProvider.TransitionActivations;

        public IReleaseSpec GetCurrentHeadSpec()
        {
            BlockHeader? header = _blockFinder.FindBestSuggestedHeader();
            ForkActivation activation = header is null
                ? (ForkActivation)0
                : new ForkActivation(header.Number, header.Timestamp);

            // Reference-type record keeps the (activation, spec) publication atomic.
            // Don't change to a record struct — 16-byte writes are not atomic.
            CachedSpec? snapshot = Volatile.Read(ref _cache);
            if (snapshot is not null && snapshot.Activation == activation)
            {
                return snapshot.Spec;
            }

            IReleaseSpec spec = _specProvider.GetSpec(activation);

            Volatile.Write(ref _cache, new CachedSpec(activation, spec));
            return spec;
        }

        private sealed record CachedSpec(ForkActivation Activation, IReleaseSpec Spec);
    }
}
