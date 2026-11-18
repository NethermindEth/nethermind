// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncAllocationStrategyFactory : StaticPeerAllocationStrategyFactory<SnapSyncBatch>, IPeerAllocationStrategyFactory<SnapSyncBatch>
    {

        private static readonly IPeerAllocationStrategy DefaultStrategy =
            new SatelliteProtocolPeerAllocationStrategy<ISnapSyncPeer>(new BySpeedStrategy(TransferSpeedType.SnapRanges, true), "snap");

        private readonly SnapRecoveryCoordinator? _recovery;
        private readonly ISnapProvider? _provider;

        /// <summary>Creates the SNAP satellite and speed allocation strategy.</summary>
        public SnapSyncAllocationStrategyFactory() : base(DefaultStrategy)
        {
        }

        internal SnapSyncAllocationStrategyFactory(SnapRecoveryCoordinator recovery, ISnapProvider provider) : base(DefaultStrategy)
            => (_recovery, _provider) = (recovery, provider);

        /// <inheritdoc/>
        public new IPeerAllocationStrategy Create(SnapSyncBatch request)
        {
            SnapRecoveryCoordinator? recovery = request.RecoveryContext ?? _recovery;
            ISnapProvider? snapProvider = request.ProviderContext ?? _provider;
            if (recovery is null) return DefaultStrategy;
            bool rooted = request.AccountRangeRequest is not null || request.StorageRangeRequest is not null || request.AccountsToRefreshRequest is not null;
            bool unknown = request.StorageRangeRequest is { } storage && (snapProvider is not SnapProvider provider || provider.IsUnknown(storage));
            return new EligibleStrategy(recovery, rooted, unknown, request.RecoveryCohort);
        }

        private sealed class EligibleStrategy(SnapRecoveryCoordinator recovery, bool rooted, bool unknown, SnapRecoveryCoordinator.Cohort? cohort) : IPeerAllocationStrategy
        {
            public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
            {
                List<PeerInfo> eligible = [];
                foreach (PeerInfo peer in peers)
                    if (recovery.CanDispatch(peer, rooted, unknown, cohort)) eligible.Add(peer);
                if (currentPeer is not null && !recovery.CanDispatch(currentPeer, rooted, unknown, cohort)) currentPeer = null;
                return DefaultStrategy.Allocate(currentPeer, eligible, nodeStatsManager, blockTree);
            }
        }
    }
}
