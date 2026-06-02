// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

public class KademliaConfig<TNode>
{
    /// <summary>
    /// The current node id
    /// </summary>
    public TNode CurrentNodeId { get; set; } = default!;

    /// <summary>
    /// K, as in the size of the kbucket.
    /// </summary>
    public int KSize { get; set; } = 16;

    /// <summary>
    /// Alpha, as in the parallelism of the lookup algorithm.
    /// </summary>
    public int Alpha { get; set; } = 3;

    /// <summary>
    /// Beta, as in B in kademlia the kademlia paper, 4.2 Accelerated Lookups
    /// </summary>
    public int Beta { get; set; } = 2;

    /// <summary>
    /// The interval on which a table refresh is initiated.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Minimum age before an individual non-empty bucket is refreshed again.
    /// </summary>
    public TimeSpan BucketRefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The timeout for each find neighbour call lookup
    /// </summary>
    public TimeSpan LookupFindNeighbourHardTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many time a request for a node failed before we remove it from the routing table.
    /// </summary>
    public int NodeRequestFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Starting boot nodes.
    /// </summary>
    public IReadOnlyList<TNode> BootNodes { get; set; } = [];
}
