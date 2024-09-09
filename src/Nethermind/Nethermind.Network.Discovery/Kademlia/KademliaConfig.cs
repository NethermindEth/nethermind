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
    /// Alpha, as in the parallelism of the lookup algorith.
    /// </summary>
    public int Alpha { get; set; }

    /// <summary>
    /// Beta, as in B in kademlia the kademlia paper, 4.2 Accelerated Lookups
    /// Only works with tree based routing table.
    /// </summary>
    public int Beta { get; set; } = 2;

    /// <summary>
    /// Use tree based routing table. False to use fixed array table.
    /// </summary>
    public bool UseTreeBasedRoutingTable { get; set; } = true;

    /// <summary>
    /// The interval on which a table refresh is initiated.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Use a different algorithm for the neighbour and value lookup.
    /// </summary>
    public bool UseNewLookup { get; set; }
}
