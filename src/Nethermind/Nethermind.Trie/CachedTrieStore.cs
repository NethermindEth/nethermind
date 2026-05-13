// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// For use with read only trie store where the node is not cached. For when using readahead flag
/// where multiple get will traverse the trie. A single trie, will have increasing read order which is
/// fine, but the second get will get back to the root of the trie, meaning the iterator for readhead flag
/// will need to seek back.
/// </summary>
/// <param name="base"></param>
public class CachedTrieStore(IScopedTrieStore @base) : IScopedTrieStore, ITrieNodeResolverSource
{
    private readonly NonBlocking.ConcurrentDictionary<(TreePath path, ValueHash256 hash), TrieNode> _cachedNode = new();

    public TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        _cachedNode.GetOrAdd((path, hash), (key, state) => state.@base.GetOrLoadNode(key.path, key.hash, state.flags), (@base, flags));

    public bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
    {
        if (_cachedNode.TryGetValue((path, hash), out node)) return true;
        if (!@base.TryGetOrLoadNode(in path, in hash, out node, flags)) return false;
        node = _cachedNode.GetOrAdd((path, hash), node);
        return true;
    }

    public byte[]? LoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        @base.LoadRlp(in path, in hash, flags);

    public byte[]? TryLoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        @base.TryLoadRlp(in path, in hash, flags);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        throw new InvalidOperationException("unsupported");

    public INodeStorage.KeyScheme Scheme => @base.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        @base.BeginCommit(root, writeFlags);

    public ITrieNodeResolver? GetReadOnlyTraversalResolver() =>
        @base is ITrieNodeResolverSource source
            && source.GetReadOnlyTraversalResolver() is ITrieNodeResolver readOnlyResolver
            ? new CachedTrieNodeResolver(readOnlyResolver)
            : null;

    private sealed class CachedTrieNodeResolver(ITrieNodeResolver inner) : ITrieNodeResolver
    {
        private readonly NonBlocking.ConcurrentDictionary<(TreePath path, ValueHash256 hash), TrieNode> _cachedNode = new();

        public TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
            _cachedNode.GetOrAdd((path, hash), (key, state) => state.inner.GetOrLoadNode(key.path, key.hash, state.flags), (inner, flags));

        public bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
        {
            if (_cachedNode.TryGetValue((path, hash), out node)) return true;
            if (!inner.TryGetOrLoadNode(in path, in hash, out node, flags)) return false;
            node = _cachedNode.GetOrAdd((path, hash), node);
            return true;
        }

        public byte[]? LoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
            inner.LoadRlp(in path, in hash, flags);

        public byte[]? TryLoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
            inner.TryLoadRlp(in path, in hash, flags);

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            new CachedTrieNodeResolver(inner.GetStorageTrieNodeResolver(address));

        public INodeStorage.KeyScheme Scheme => inner.Scheme;
    }
}
