// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
public class CachedTrieStore(IScopedTrieStore @base) : IScopedTrieStore
{
    private readonly NonBlocking.ConcurrentDictionary<(TreePath path, Hash256 hash), TrieNode> _cachedNode = new();

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        _cachedNode.GetOrAdd((path, hash), (key) => @base.FindCachedOrUnknown(key.path, key.hash));

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        @base.LoadRlp(in path, hash, flags);

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        @base.TryLoadRlp(in path, hash, flags);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        throw new InvalidOperationException("unsupported");

    public INodeStorage.KeyScheme Scheme => @base.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        @base.BeginCommit(root, writeFlags);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => @base.IsPersisted(in path, in keccak);
}

