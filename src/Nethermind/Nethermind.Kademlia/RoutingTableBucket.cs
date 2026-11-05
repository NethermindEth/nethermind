// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Snapshot of one routing-table bucket.
/// </summary>
/// <param name="Prefix">Bucket prefix used by the routing table.</param>
/// <param name="Distance">Bucket depth, expressed as the distance index used by the routing table traversal.</param>
/// <param name="Nodes">Snapshot of nodes in this bucket. Mutating the collection does not mutate the routing table.</param>
public readonly record struct RoutingTableBucket<TNode, TKadKey>(TKadKey Prefix, int Distance, IReadOnlyList<TNode> Nodes)
    where TNode : notnull
    where TKadKey : notnull
{
    /// <summary>
    /// Number of nodes captured in the bucket snapshot.
    /// </summary>
    public int Count => Nodes.Count;
}
