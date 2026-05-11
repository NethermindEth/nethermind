// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public sealed class ScopedTrieStore(IScopableTrieStore fullTrieStore, Hash256? address)
    : IScopedTrieStore, ITrieNodeResolverSource
{
    public TrieNode FindCachedOrUnknown(in TreePath path, in ValueHash256 hash) =>
        fullTrieStore.FindCachedOrUnknown(address, path, in hash);

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
