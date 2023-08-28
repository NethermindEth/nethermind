// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleSyncAllocationStrategyFactory: StaticPeerAllocationStrategyFactory<VerkleSyncBatch>
{
    private static readonly IPeerAllocationStrategy DefaultStrategy =
        new SatelliteProtocolPeerAllocationStrategy<IVerkleSyncPeer>(new TotalDiffStrategy(new BySpeedStrategy(TransferSpeedType.VerkleSyncRanges, true), TotalDiffStrategy.TotalDiffSelectionType.CanBeSlightlyWorse), "verkle");

    public VerkleSyncAllocationStrategyFactory() : base(DefaultStrategy)
    {
    }

}
