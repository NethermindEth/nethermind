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

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null) =>
            _specProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

        public ForkActivation? MergeBlockNumber => _specProvider.MergeBlockNumber;

        public ulong TimestampFork => _specProvider.TimestampFork;

        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;

        public IReleaseSpec GenesisSpec => _specProvider.GenesisSpec;

        public IReleaseSpec GetSpec(ForkActivation forkActivation) => _specProvider.GetSpec(forkActivation);

        public long? DaoBlockNumber => _specProvider.DaoBlockNumber;

        public ulong? BeaconChainGenesisTimestamp => _specProvider.BeaconChainGenesisTimestamp;

        public ulong NetworkId => _specProvider.NetworkId;

        public ulong ChainId => _specProvider.ChainId;

        public ForkActivation[] TransitionActivations => _specProvider.TransitionActivations;

        public IReleaseSpec GetCurrentHeadSpec()
        {
            BlockHeader? header = _blockFinder.FindBestSuggestedHeader();
            long headerNumber = header?.Number ?? 0;

            // Lock-free fast path: a single reference read returns a fully constructed
            // (number, spec) pair, so readers never observe a torn pairing.
            CachedSpec? snapshot = Volatile.Read(ref _cache);
            if (snapshot is not null && snapshot.Number == headerNumber)
            {
                return snapshot.Spec;
            }

            IReleaseSpec spec = header is not null
                ? _specProvider.GetSpec(header)
                : _specProvider.GetSpec((ForkActivation)headerNumber);

            // Concurrent writers race to publish a snapshot for the same headerNumber.
            // Because GetSpec is deterministic per (number, header), every winner stores
            // an equivalent value, so a last-writer-wins publish is safe.
            Volatile.Write(ref _cache, new CachedSpec(headerNumber, spec));
            return spec;
        }

        private sealed record CachedSpec(long Number, IReleaseSpec Spec);
    }
}
