// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

/// <summary>
/// Per-shard tracker that counts active (leased) <see cref="RefCountingTrieNode"/> instances.
/// Delegates object pooling and per-type counting to a shared <see cref="RefCountingTrieNodePool"/>.
/// </summary>
public sealed class RefCountingRlpNodePoolTracker(RefCountingTrieNodePool pool)
{
    private int _activeCount;

    /// <summary>Number of nodes currently leased out through this tracker.</summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>
    /// Rents a node via the shared pool, initializes it with the given hash and RLP,
    /// and returns it with a single lease. Caller must dispose when done.
    /// </summary>
    public RefCountingTrieNode Rent(ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        RefCountingTrieNode node = pool.Rent(this, hash, rlp);
        Interlocked.Increment(ref _activeCount);
        return node;
    }

    /// <summary>
    /// Returns a node through this tracker. Decrements the active count and returns the node to the shared pool.
    /// Called by <see cref="RefCountingTrieNode.CleanUp"/> on final dispose.
    /// </summary>
    internal void Return(RefCountingTrieNode node)
    {
        Interlocked.Decrement(ref _activeCount);
        pool.Return(node);
    }
}
