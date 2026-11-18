// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

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

    /// <summary>Maximum requests per lookup. Defaults to 128.</summary>
    public int LookupQueryLimit { get; set; } = 128;

    /// <summary>Maximum distinct candidate IDs retained per lookup. Defaults to 2048.</summary>
    public int LookupSeenLimit { get; set; } = 2048;

    /// <summary>Clock used for the lookup deadline.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Maximum lookup duration, including requests. Defaults to 30 seconds.</summary>
    public TimeSpan LookupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many time a request for a node failed before we remove it from the routing table.
    /// </summary>
    public int NodeRequestFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Maximum time to wait for active refresh pings while disposing the node health tracker.
    /// </summary>
    public TimeSpan RefreshPingTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delay before pinging the oldest node in a full bucket to give incoming traffic a chance to refresh it first.
    /// </summary>
    public TimeSpan RefreshPingDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Minimum interval between refresh probes triggered by full-bucket insertions.
    /// </summary>
    /// <remarks>
    /// The default five-failure threshold and 30-second interval permit the fifth probe 120 seconds after the first.
    /// Probes are insertion-driven, so retiring a silent bucket tail can take longer when insertions are infrequent.
    /// </remarks>
    public TimeSpan RefreshRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Compares the network endpoints of contacts with the same routing key.
    /// </summary>
    public Func<TNode, TNode, bool> AreSameEndpoint { get; set; } = EqualityComparer<TNode>.Default.Equals;

    /// <summary>
    /// Identifies contacts whose endpoints have been directly verified.
    /// </summary>
    public Func<TNode, bool> IsVerified { get; set; } = static _ => true;

    /// <summary>
    /// Requires a fresh liveness probe before promoting a replacement contact.
    /// </summary>
    public bool RequireReplacementValidation { get; set; }

    /// <summary>
    /// Maps a contact to its subnet quota group, or null for an exempt contact.
    /// </summary>
    public Func<TNode, UInt128?>? GetSubnet { get; set; }

    /// <summary>
    /// Maximum stored contacts from one subnet in a leaf, including replacements.
    /// </summary>
    public int BucketSubnetLimit { get; set; } = 2;

    /// <summary>
    /// Maximum stored contacts from one subnet across the table, including replacements.
    /// </summary>
    public int TableSubnetLimit { get; set; } = 10;

    /// <summary>
    /// Starting boot nodes.
    /// </summary>
    public IReadOnlyList<TNode> BootNodes { get; set; } = [];

    /// <summary>
    /// Merges the previously stored value into an incoming value with the same routing key.
    /// The function returns the value to store and runs while the routing table is locked.
    /// </summary>
    /// <remarks>
    /// The merge must return promptly, must not perform I/O or mutate the routing table that invoked it, and must not
    /// acquire a lock that callers can hold while entering that routing table (which would invert the lock order).
    /// </remarks>
    public Func<TNode, TNode, TNode>? MergeOnRefresh { get; set; }
}
