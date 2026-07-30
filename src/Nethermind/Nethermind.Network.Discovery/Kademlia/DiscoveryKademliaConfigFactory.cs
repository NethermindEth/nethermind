// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

internal static class DiscoveryKademliaConfigFactory
{
    public static KademliaConfig<Node> Create(Node currentNode, IReadOnlyList<Node> bootNodes, IDiscoveryConfig discoveryConfig)
        => new()
        {
            CurrentNodeId = currentNode,
            KSize = discoveryConfig.BucketSize,
            Alpha = discoveryConfig.Concurrency,
            Beta = discoveryConfig.BitsPerHop,
            LookupFindNeighbourHardTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout + discoveryConfig.BondWaitTime + (2L * discoveryConfig.SendNodeTimeout)),
            RefreshPingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout),
            RefreshInterval = TimeSpan.FromMilliseconds(discoveryConfig.DiscoveryInterval),
            BootNodes = bootNodes
        };
}
