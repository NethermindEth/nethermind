// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Admission policy for nodes entering the routing table.
/// </summary>
/// <remarks>
/// Lock order is routing-table lock, then limiter lock. <see cref="TryAdmit"/> is called under the routing-table
/// lock, so an implementation must not call back into the table. The bucket scan receives the bucket's cached array,
/// which allocates only when the bucket size exceeds the replacement cache size.
/// </remarks>
public interface IRoutingTableNodeLimiter<TNode>
{
    /// <summary>Admits <paramref name="node"/> to the bucket whose active entries are <paramref name="bucketNodes"/>.</summary>
    /// <remarks>Called under the routing-table lock. Must not call back into the table. An exempt verdict releases
    /// any charge the node previously held, so a node that gains an exemption stops consuming its subnet slot.</remarks>
    bool TryAdmit(TNode node, ReadOnlySpan<TNode> bucketNodes);

    /// <summary>Releases the slot held by <paramref name="node"/>, if any. Idempotent.</summary>
    void Release(TNode node);

    /// <summary>Table-wide probe: whether <see cref="TryAdmit"/> would find a table slot. Does not reserve.</summary>
    bool WouldAdmit(TNode node);
}
