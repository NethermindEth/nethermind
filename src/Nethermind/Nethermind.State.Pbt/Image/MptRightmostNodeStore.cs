// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.Image;

/// <summary>Trie node store for folding strictly ascending MPT leaves in windows; it retains only the rightmost node at each depth.</summary>
/// <remarks>
/// A key above every committed key reaches an existing node only when that node's path prefixes the largest committed
/// key, so the rightmost node at each depth is the only one a later window can read, and every other write is dropped.
/// </remarks>
internal sealed class MptRightmostNodeStore : IScopedTrieStore
{
    internal const int DefaultWindowSize = 1_000_000;

    private readonly Lock _lock = new();
    private readonly Node[] _edge = new Node[65];

    /// <summary>Calculates the MPT root of strictly ascending keyed leaves.</summary>
    /// <param name="windowSize">Maximum leaves set per commit.</param>
    internal static ValueHash256 CalculateRoot(IEnumerable<KeyValuePair<ValueHash256, byte[]>> entries, int windowSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        cancellationToken.ThrowIfCancellationRequested();
        PatriciaTree tree = new(new MptRightmostNodeStore(), NullLogManager.Instance);
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> window = new(Math.Min(windowSize, 4096));
        ValueHash256? previous = null;
        foreach (KeyValuePair<ValueHash256, byte[]> entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previous is { } previousKey && previousKey.CompareTo(entry.Key) >= 0)
                throw new InvalidDataException("MPT keys must be strictly increasing.");
            previous = entry.Key;
            window.Add(new PatriciaTree.BulkSetEntry(entry.Key, entry.Value));
            if (window.Count != windowSize) continue;
            Fold(tree, window);
            window.Clear();
        }
        Fold(tree, window);
        return tree.RootHash.ValueHash256;

        static void Fold(PatriciaTree tree, in ArrayPoolListRef<PatriciaTree.BulkSetEntry> window)
        {
            if (window.Count == 0) return;
            tree.BulkSet(window, PatriciaTree.Flags.WasSorted | PatriciaTree.Flags.DoNotParallelize);
            // The commit resets the root to an unresolved node, so the next window reads the right edge from the store.
            tree.Commit();
        }
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        lock (_lock)
        {
            Node node = _edge[path.Length];
            return node.Rlp is not null && node.Hash == hash && node.Path.Equals(path) ? node.Rlp : null;
        }
    }

    public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        TryLoadRlp(path, hash, flags) ?? throw new TrieNodeException($"Trie node {path}:{hash} is not on the right edge.", path, hash);

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(this);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => this;

    public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;

    private void Keep(in TreePath path, TrieNode node)
    {
        if (node.Keccak is null) return;
        lock (_lock)
        {
            ref Node edge = ref _edge[path.Length];
            if (edge.Rlp is not null && path.CompareTo(edge.Path) < 0) return;
            edge = new(path, node.Keccak, node.FullRlp.ToArray()!);
        }
    }

    private sealed class Committer(MptRightmostNodeStore store) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            store.Keep(path, node);
            return node;
        }

        public void Dispose() { }
    }

    private readonly record struct Node(TreePath Path, Hash256 Hash, byte[]? Rlp);
}
