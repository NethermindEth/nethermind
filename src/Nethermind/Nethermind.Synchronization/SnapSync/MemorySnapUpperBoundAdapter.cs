// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// A wrapper to trie store that prevent committing boundary proof node and nodes whose subtree extend beyond
/// UpperBound or before LowerBound. This is to prevent double writes on partitioned snap ranges.
/// </summary>
/// <param name="baseTrieStore"></param>
/// <param name="boundWholePath">
/// Whether a leaf or an extension is bounded by its whole path, as a branch is, rather than by its keys. A node stands
/// for every key under its path, so one reaching past the ranges downloaded at its root would hide the entries another
/// root leaves there from healing.
/// </param>
internal sealed class MemorySnapUpperBoundAdapter(IScopedTrieStore baseTrieStore, bool boundWholePath) : IScopedTrieStore
{
    public ValueHash256 LowerBound = ValueKeccak.Zero;
    public ValueHash256 UpperBound = ValueKeccak.MaxValue;

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => baseTrieStore.FindCachedOrUnknown(in path, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => baseTrieStore.LoadRlp(in path, hash, flags);

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => baseTrieStore.TryLoadRlp(in path, hash, flags);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new NotSupportedException("Get storage trie node resolver not supported");


    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new BoundedSnapCommitter(baseTrieStore.BeginCommit(root, writeFlags), boundWholePath, LowerBound, UpperBound);

    private sealed class BoundedSnapCommitter(ICommitter baseCommitter, bool boundWholePath, ValueHash256 subtreeLowerLimit, ValueHash256 subtreeLimit) : ICommitter
    {
        public void Dispose() => baseCommitter.Dispose();

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            if (node.IsBoundaryProofNode) return node;

            ValueHash256 subtreeUpperRange = node.IsBranch || boundWholePath ? path.ToUpperBoundPath() : path.Append(node.Key).ToUpperBoundPath();
            if (subtreeUpperRange > subtreeLimit || path.ToLowerBoundPath() < subtreeLowerLimit) return node;

            node = baseCommitter.CommitNode(ref path, node);
            node.IsPersisted = true;
            return node;
        }
    }
}
