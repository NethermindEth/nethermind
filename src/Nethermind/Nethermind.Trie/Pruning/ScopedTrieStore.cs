// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public sealed class ScopedTrieStore(IScopableTrieStore fullTrieStore, Hash256? address)
    : IScopedTrieStore, ITrieNodeResolverSource
{
    public TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.GetOrLoadNode(address, in path, in hash, flags);

    public bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.TryGetOrLoadNode(address, in path, in hash, out node, flags);

    public bool TryGetCachedNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node)
        => fullTrieStore.TryGetCachedNode(address, in path, in hash, out node);

    public byte[]? LoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.LoadRlp(address, path, in hash, flags);

    public byte[]? TryLoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.TryLoadRlp(address, path, in hash, flags);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address1) =>
        address1 == address ? this : new ScopedTrieStore(fullTrieStore, address1);

    public INodeStorage.KeyScheme Scheme => fullTrieStore.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        fullTrieStore.BeginCommit(address, root, writeFlags);

    public ITrieNodeResolver? GetReadOnlyTraversalResolver() =>
        fullTrieStore is IScopedReadOnlyTraversalProvider provider
            ? provider.GetReadOnlyTraversalResolver(address)
            : null;
}
