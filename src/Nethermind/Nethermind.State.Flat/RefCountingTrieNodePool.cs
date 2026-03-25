// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

/// <summary>
/// Pools <see cref="RefCountingTrieNode"/> instances and tracks active (leased) count.
/// Each <see cref="TrieNodeCache"/> shard has its own pool for per-shard memory tracking.
/// </summary>
public sealed class RefCountingTrieNodePool
{
    private readonly ConcurrentStack<RefCountingTrieNode> _pool = new();
    private int _activeCount;
    private readonly int _maxPooled;

    /// <summary>Number of nodes currently leased out (not in the pool).</summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    public RefCountingTrieNodePool(int maxPooled = 4096) =>
        _maxPooled = maxPooled;

    /// <summary>
    /// Rents a node from the pool (or creates a new one), initializes it with the given hash and RLP,
    /// and returns it with a single lease. Caller must dispose when done.
    /// </summary>
    public RefCountingTrieNode Rent(ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        if (!_pool.TryPop(out RefCountingTrieNode? node))
        {
            node = new RefCountingTrieNode(this);
        }

        Interlocked.Increment(ref _activeCount);
        node.Initialize(hash, rlp);
        return node;
    }

    /// <summary>
    /// Returns a node to the pool. Called by <see cref="RefCountingTrieNode.CleanUp"/> on final dispose.
    /// </summary>
    internal void Return(RefCountingTrieNode node)
    {
        Interlocked.Decrement(ref _activeCount);
        if (_pool.Count < _maxPooled)
        {
            _pool.Push(node);
        }
    }
}
