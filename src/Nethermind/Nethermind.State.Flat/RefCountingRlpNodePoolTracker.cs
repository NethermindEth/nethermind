// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Per-shard tracker that counts active (leased) <see cref="RefCountingTrieNode"/> instances.
/// Delegates actual object pooling to a shared <see cref="RefCountingTrieNodePool"/>.
/// </summary>
public sealed class RefCountingRlpNodePoolTracker(RefCountingTrieNodePool pool)
{
    // Shell: ~128 (PaddedValue) + 8 (tracker) + 32 (Hash) + 4 (NodeType) + 8 (_nodeImpl) + 16 (obj header) = 196, aligned to 200
    private const int ShellSize = 200;
    // Impl sizes: obj header (16) + fields, aligned to 8
    private const int BranchImplSize = 608;    // 16 (header) + 2 (Length) + 544 (RLP) + 34 (17 offsets) + padding → 608
    private const int ExtensionImplSize = 128; // 16 (header) + 2 (Length) + 96 (RLP) + 2 (offset) + padding → 128
    private const int LeafImplSize = 184;      // 16 (header) + 2 (Length) + 160 (RLP) + padding → 184

    private int _activeBranchCount;
    private int _activeExtensionCount;
    private int _activeLeafCount;

    /// <summary>Total number of nodes currently leased out through this tracker.</summary>
    public int ActiveCount => Volatile.Read(ref _activeBranchCount) + Volatile.Read(ref _activeExtensionCount) + Volatile.Read(ref _activeLeafCount);

    public int ActiveBranchCount => Volatile.Read(ref _activeBranchCount);
    public int ActiveExtensionCount => Volatile.Read(ref _activeExtensionCount);
    public int ActiveLeafCount => Volatile.Read(ref _activeLeafCount);

    /// <summary>Estimated memory used by all active nodes tracked by this shard.</summary>
    public long ActiveMemory =>
        (long)Volatile.Read(ref _activeBranchCount) * (ShellSize + BranchImplSize) +
        (long)Volatile.Read(ref _activeExtensionCount) * (ShellSize + ExtensionImplSize) +
        (long)Volatile.Read(ref _activeLeafCount) * (ShellSize + LeafImplSize);

    /// <summary>
    /// Rents a node via the shared pool, initializes it with the given hash and RLP,
    /// and returns it with a single lease. Caller must dispose when done.
    /// </summary>
    public RefCountingTrieNode Rent(ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        RefCountingTrieNode node = pool.Rent(this, hash, rlp);
        IncrementTypeCount(node.NodeType);
        return node;
    }

    /// <summary>
    /// Returns a node through this tracker. Decrements the active count and returns the node to the shared pool.
    /// Called by <see cref="RefCountingTrieNode.CleanUp"/> on final dispose.
    /// </summary>
    internal void Return(RefCountingTrieNode node)
    {
        DecrementTypeCount(node.NodeType);
        pool.Return(node);
    }

    private void IncrementTypeCount(NodeType nodeType)
    {
        switch (nodeType)
        {
            case NodeType.Branch: Interlocked.Increment(ref _activeBranchCount); break;
            case NodeType.Extension: Interlocked.Increment(ref _activeExtensionCount); break;
            default: Interlocked.Increment(ref _activeLeafCount); break;
        }
    }

    private void DecrementTypeCount(NodeType nodeType)
    {
        switch (nodeType)
        {
            case NodeType.Branch: Interlocked.Decrement(ref _activeBranchCount); break;
            case NodeType.Extension: Interlocked.Decrement(ref _activeExtensionCount); break;
            default: Interlocked.Decrement(ref _activeLeafCount); break;
        }
    }
}
