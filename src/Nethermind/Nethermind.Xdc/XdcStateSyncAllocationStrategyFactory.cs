// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.StateSync;
using Nethermind.Xdc.P2P;

namespace Nethermind.Xdc;

public class XdcStateSyncAllocationStrategyFactory : StaticPeerAllocationStrategyFactory<StateSyncBatch>
{
    private static readonly IPeerAllocationStrategy DefaultStrategy =
        new AllocationStrategy(new BySpeedStrategy(TransferSpeedType.NodeData, true));

    public XdcStateSyncAllocationStrategyFactory() : base(DefaultStrategy)
    {
    }

    internal class AllocationStrategy(IPeerAllocationStrategy strategy) : FilterPeerAllocationStrategy(strategy)
    {
        // Every XDC version serves GetNodeData - the eth/67 removal was never applied - but PeerInfoExtensions
        // gates that on a mainline version comparison that XDC's numbering sits above.
        protected override bool Filter(PeerInfo peerInfo) =>
            peerInfo.CanGetTrieNodes() || XdcProtocolVersions.IsXdcVersion(peerInfo.SyncPeer.ProtocolVersion);
    }
}

