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

    /// <summary>
    /// Validates a node taken from a bucket's replacement cache before it is promoted into the bucket.
    /// </summary>
    /// <remarks>
    /// The function runs while the routing table is locked. The merge contract applies here too: it must return
    /// promptly, must not throw, must not perform I/O or mutate the routing table that invoked it, and must not
    /// acquire a lock that callers can hold while entering that routing table (which would invert the lock order).
    /// Returning <see langword="false"/> discards the cached entry.
    /// </remarks>
    public Func<TNode, bool>? CanPromoteReplacement { get; set; }

    /// <summary>
    /// Decides whether a node which answered a refresh ping may still renew its bucket entry.
    /// </summary>
    /// <remarks>
    /// Read on every successful refresh ping rather than captured at construction, because discovery installs it
    /// after the tracker exists. It runs on a thread-pool task with no cancellation token, and is expected to both
    /// decide and record the admission, like the inbound-message gate; discovery therefore passes the address
    /// filter's admission rather than a pure predicate. The function must return promptly, must not throw, must not
    /// perform I/O or mutate the routing table. Returning <see langword="false"/> rejects the refresh while its
    /// eviction is still pending; an incoming message from the node can clear that pending refresh first, in which
    /// case the entry is not evicted. <see langword="null"/> allows the refresh.
    /// </remarks>
    public Func<TNode, bool>? CanRefreshNode { get; set; }
}
