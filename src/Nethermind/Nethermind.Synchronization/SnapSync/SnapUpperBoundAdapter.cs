// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// A wrapper to trie store that prevent committing boundary proof node and nodes whose subtree extend beyond
/// UpperBound. This is to prevent double writes on partitioned snap ranges.
/// </summary>
/// <param name="baseTrieStore"></param>
public class SnapUpperBoundAdapter(IScopedTrieStore baseTrieStore) : IScopedTrieStore
{
    public ValueHash256 UpperBound = ValueKeccak.MaxValue;

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        ThrowMissingProofNode(path, hash);

    [DoesNotReturn, StackTraceHidden]
    private static byte[] ThrowMissingProofNode(in TreePath path, Hash256 hash) =>
        throw new MissingTrieNodeException("Node missing from range proof", null, path, hash);

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => null;

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new NotSupportedException("Get storage trie node resolver not supported");

    public INodeStorage.KeyScheme Scheme => baseTrieStore.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new BoundedSnapCommitter(baseTrieStore.BeginCommit(root, writeFlags), UpperBound);

    internal static bool IsWithinUpperBound(TrieNode node, in TreePath path, in ValueHash256 upperBound) =>
        (node.IsBranch ? path : path.Append(node.Key)).ToUpperBoundPath() <= upperBound;

    private sealed class BoundedSnapCommitter(ICommitter baseCommitter, ValueHash256 subtreeLimit) : ICommitter
    {
        public void Dispose() => baseCommitter.Dispose();

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            if (node.IsBoundaryProofNode || !IsWithinUpperBound(node, path, subtreeLimit)) return node;

            node = baseCommitter.CommitNode(ref path, node);
            node.IsPersisted = true;
            return node;
        }
    }
}
