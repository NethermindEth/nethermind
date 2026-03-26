// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>
/// Holds leases on <see cref="RefCountingTrieNode"/> instances to keep their RLP byte[] alive
/// for the duration of a block. On <see cref="Reset"/>, all held leases are released.
/// If exhausted, callers fall back to copying.
/// Lives at the scope provider level and is reused across blocks.
/// </summary>
public sealed class RefCountingNodeLeasePool(int initialCapacity = 4096)
{
    private RefCountingTrieNode?[] _nodes = new RefCountingTrieNode?[initialCapacity];
    private int _index;

    /// <summary>
    /// Acquires a lease on the node and registers it. Returns true if registered.
    /// Returns false if the pool is exhausted — caller should fall back to copying.
    /// </summary>
    public bool TryHoldLease(RefCountingTrieNode node)
    {
        int idx = Interlocked.Increment(ref _index) - 1;
        RefCountingTrieNode?[] nodes = _nodes;
        if ((uint)idx >= (uint)nodes.Length) return false;

        node.AcquireLease();
        nodes[idx] = node;
        return true;
    }

    /// <summary>
    /// Releases all held leases and resets for next block. Expands if previous block exceeded capacity.
    /// </summary>
    public void Reset()
    {
        int used = Volatile.Read(ref _index);
        RefCountingTrieNode?[] nodes = _nodes;

        for (int i = 0; i < Math.Min(used, nodes.Length); i++)
        {
            RefCountingTrieNode? node = nodes[i];
            if (node is not null)
            {
                node.Dispose();
                nodes[i] = null;
            }
        }

        if (used > nodes.Length)
        {
            int newCapacity = used + (used >> 1);
            _nodes = new RefCountingTrieNode?[newCapacity];
        }

        _index = 0;
    }
}
