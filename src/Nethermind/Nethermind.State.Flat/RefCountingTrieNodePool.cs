// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.State.Flat;

/// <summary>
/// Shared object pool for <see cref="RefCountingTrieNode"/> instances.
/// Handles only object reuse — active-node tracking is done by <see cref="RefCountingRlpNodePoolTracker"/>.
/// </summary>
public sealed class RefCountingTrieNodePool(int maxPooled = 4096)
{
    private readonly ConcurrentStack<RefCountingTrieNode> _pool = new();

    /// <summary>
    /// Rents a node from the pool (or creates a new one) bound to the given tracker.
    /// The node is not yet initialized — the tracker handles initialization.
    /// </summary>
    internal RefCountingTrieNode Rent(RefCountingRlpNodePoolTracker tracker)
    {
        if (!_pool.TryPop(out RefCountingTrieNode? node))
        {
            node = new RefCountingTrieNode(tracker);
        }
        else
        {
            node.SetTracker(tracker);
        }

        return node;
    }

    /// <summary>
    /// Returns a node to the pool. Called by <see cref="RefCountingRlpNodePoolTracker.Return"/>.
    /// </summary>
    internal void Return(RefCountingTrieNode node)
    {
        if (_pool.Count < maxPooled)
        {
            _pool.Push(node);
        }
    }
}
