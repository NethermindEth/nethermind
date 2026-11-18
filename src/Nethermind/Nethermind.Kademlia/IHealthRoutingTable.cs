// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Kademlia;

/// <summary>
/// Supports atomic health checks and routing-table mutations with deferred notifications.
/// </summary>
/// <remarks>
/// Implementations serialize their mutations internally. Every active-entry refresh changes its opaque
/// revision, including a refresh with the same node value. Deferred operations must not invoke callbacks;
/// callers publish after releasing health serialization. Verified liveness resets failure history through
/// <see cref="INodeHealthTracker{TNode}"/>; direct table refreshes invalidate outstanding removals.
/// </remarks>
public interface IHealthRoutingTable<TNode, TKadKey> : IRoutingTable<TNode, TKadKey>
    where TNode : notnull
    where TKadKey : notnull
{
    /// <summary>Snapshots an active node and its opaque revision atomically.</summary>
    bool TryGetActive(in TKadKey hash, [MaybeNullWhen(false)] out TNode node, [NotNullWhen(true)] out object? revision);

    /// <summary>Adds or refreshes a node without publishing notifications.</summary>
    BucketAddResult AddOrRefreshDeferred(in TKadKey hash, TNode node, out TNode? toRefresh, out RoutingTableNotifications<TNode> notifications);

    /// <summary>Removes the expected active node only if its revision still matches, without publishing notifications.</summary>
    bool Remove(in TKadKey hash, TNode expected, object revision, out RoutingTableNotifications<TNode> notifications);

    /// <summary>Publishes deferred notifications after table and health serialization have been released.</summary>
    void Publish(in RoutingTableNotifications<TNode> notifications);
}

/// <summary>Carries deferred node addition and removal notifications.</summary>
/// <param name="HasAdded">Whether an addition notification is present.</param>
/// <param name="Added">The added node.</param>
/// <param name="HasRemoved">Whether a removal notification is present.</param>
/// <param name="Removed">The removed node.</param>
public readonly record struct RoutingTableNotifications<TNode>(bool HasAdded, TNode? Added, bool HasRemoved, TNode? Removed);
