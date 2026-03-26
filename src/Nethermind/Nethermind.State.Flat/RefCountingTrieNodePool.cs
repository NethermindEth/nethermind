// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;

namespace Nethermind.State.Flat;

/// <summary>
/// Shared object pool for <see cref="RefCountingTrieNode"/> instances.
/// Handles only object reuse — active-node tracking is done by <see cref="RefCountingRlpNodePoolTracker"/>.
/// </summary>
public sealed class RefCountingTrieNodePool
{
    private readonly ObjectPool<RefCountingTrieNode> _pool;

    public RefCountingTrieNodePool(int maxPooled = 4096) =>
        _pool = new DefaultObjectPool<RefCountingTrieNode>(new Policy(), maxPooled);

    /// <summary>
    /// Rents a node from the pool (or creates a new one) bound to the given tracker.
    /// The node is not yet initialized — the tracker handles initialization.
    /// </summary>
    internal RefCountingTrieNode Rent(RefCountingRlpNodePoolTracker tracker)
    {
        RefCountingTrieNode node = _pool.Get();
        node.SetTracker(tracker);
        return node;
    }

    /// <summary>
    /// Returns a node to the pool. Called by <see cref="RefCountingRlpNodePoolTracker.Return"/>.
    /// </summary>
    internal void Return(RefCountingTrieNode node) =>
        _pool.Return(node);

    private sealed class Policy : PooledObjectPolicy<RefCountingTrieNode>
    {
        public override RefCountingTrieNode Create() => new();
        public override bool Return(RefCountingTrieNode obj) => true;
    }
}
