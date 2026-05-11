// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public sealed class ScopedTrieStore(IScopableTrieStore fullTrieStore, Hash256? address)
    : IScopedTrieStore, ITrieNodeResolverSource
{
    public TrieNode FindCachedOrUnknown(in TreePath path, in ValueHash256 hash) =>
        fullTrieStore.FindCachedOrUnknown(address, path, in hash);

    public bool TryGetCachedNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        // Forward to the underlying TrieStore's resolved-only cache lookup if available.
        // For non-TrieStore wrappers, fall back to the default (returns false).
        if (fullTrieStore is TrieStore trieStore)
        {
            return trieStore.TryGetCachedNode(address, in path, in hash, out node);
        }
        node = null;
        return false;
    }

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
