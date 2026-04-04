// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public sealed class ScopedTrieStore(IScopableTrieStore fullTrieStore, Hash256? address) : IScopedTrieStore, ITrieNodeResolverFactory
{
    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        fullTrieStore.FindCachedOrUnknown(address, path, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.LoadRlp(address, path, hash, flags);

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.TryLoadRlp(address, path, hash, flags);

    public INodeStorage.KeyScheme Scheme => fullTrieStore.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        fullTrieStore.BeginCommit(address, root, writeFlags);

    ITrieNodeResolver ITrieNodeResolverFactory.GetStorageTrieNodeResolver(Hash256? storageAddress) =>
        fullTrieStore.GetStorageTrieNodeResolver(storageAddress);
}
