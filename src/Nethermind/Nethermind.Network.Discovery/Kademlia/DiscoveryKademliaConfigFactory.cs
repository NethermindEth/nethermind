// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

internal static class DiscoveryKademliaConfigFactory
{
    public static KademliaConfig<Node> Create(PublicKey masterNode, IReadOnlyList<Node> bootNodes, IDiscoveryConfig discoveryConfig)
        => new()
        {
            // The table only needs the local node identity here; its endpoint is never contacted.
            CurrentNodeId = new Node(masterNode, "127.0.0.1", 9999, true),
            KSize = discoveryConfig.BucketSize,
            Alpha = discoveryConfig.Concurrency,
            Beta = discoveryConfig.BitsPerHop,
            LookupFindNeighbourHardTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout + discoveryConfig.BondWaitTime + (2L * discoveryConfig.SendNodeTimeout)),
            RefreshPingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout),
            RefreshInterval = TimeSpan.FromMilliseconds(discoveryConfig.DiscoveryInterval),
            BootNodes = bootNodes
        };
}
