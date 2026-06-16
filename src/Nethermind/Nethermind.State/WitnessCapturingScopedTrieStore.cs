// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Thread-safe collector of trie-node RLPs read while recomputing a block's post-state root, keyed (and
/// deduplicated) by node hash. Shared by the state-trie and all storage-trie commits of a single witness scope.
/// </summary>
/// <remarks>
/// A <see cref="ConcurrentDictionary{TKey,TValue}"/> is required because the commit runs multi-threaded
/// (the flat <c>UpdateRootHashesMultiThread</c> path and the trie backend's per-storage-tree task commits).
/// </remarks>
public sealed class WitnessNodeSink
{
    private readonly ConcurrentDictionary<Hash256AsKey, byte[]> _nodes = new();

    public void Capture(Hash256 hash, byte[] rlp) => _nodes.TryAdd(hash, rlp);

    /// <summary>The captured nodes. Read once after all commits complete (single-threaded).</summary>
    public IReadOnlyList<byte[]> Nodes
    {
        get
        {
            List<byte[]> nodes = new(_nodes.Count);
            foreach (KeyValuePair<Hash256AsKey, byte[]> kvp in _nodes)
                nodes.Add(kvp.Value);
            return nodes;
        }
    }
}

/// <summary>
/// Decorates an <see cref="IScopedTrieStore"/> so that every node read while a Patricia trie is being
/// updated/committed is recorded into a shared <see cref="WitnessNodeSink"/>. This captures the structural
/// (sibling) nodes a stateless verifier needs to re-apply the block's writes and recompute the post-state
/// root — nodes a touched-key proof walk does not include (e.g. the surviving sibling when a deletion
/// collapses a branch).
/// </summary>
public sealed class WitnessCapturingScopedTrieStore(IScopedTrieStore inner, WitnessNodeSink sink) : IScopedTrieStore
{
    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = inner.FindCachedOrUnknown(in path, hash);
        // An Unknown node carries no in-memory RLP yet; it is captured later when ResolveNode -> TryLoadRlp
        // loads it.
        if (node.NodeType == NodeType.Unknown || node.Keccak is null) return node;

        CappedArray<byte> rlp = node.FullRlp;
        if (rlp.IsNull) return node;
        sink.Capture(node.Keccak, rlp.ToArray());

        // The backend (notably the flat bundle) may serve a resolved node with its children already linked
        // as in-memory TrieNodes, so the trie reaches a child (e.g. a deletion's collapse sibling) without
        // ever calling the resolver — leaving that child uncaptured. Return an unresolved clone that carries
        // only this node's RLP: re-parsing it yields hash-ref children, forcing every child the trie actually
        // navigates to back through this wrapper, where it is captured.
        return new TrieNode(NodeType.Unknown, node.Keccak, rlp);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = inner.TryLoadRlp(in path, hash, flags);
        if (rlp is not null) sink.Capture(hash, rlp);
        return rlp;
    }

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = inner.LoadRlp(in path, hash, flags);
        if (rlp is not null) sink.Capture(hash, rlp);
        return rlp;
    }

    // Storage-trie reads during a state-tree descent must land in the same sink.
    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        inner.GetStorageTrieNodeResolver(address) is IScopedTrieStore scoped
            ? new WitnessCapturingScopedTrieStore(scoped, sink)
            : inner.GetStorageTrieNodeResolver(address);

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => inner.BeginCommit(root, writeFlags);

    public INodeStorage.KeyScheme Scheme => inner.Scheme;
}
