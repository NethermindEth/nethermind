// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Common skeleton for shared read-only traversal resolvers. Forwards LoadRlp/TryLoadRlp/Scheme
/// to the wrapping full store (so layered behavior like pre-block caching or witness capture is
/// preserved on the read path) and asks the derived class for the cached-node lookup and the
/// per-address rebuild.
/// </summary>
public abstract class ReadOnlyTraversalResolverBase(IScopableTrieStore fullTrieStore, Hash256? address) : ITrieNodeResolver
{
    protected Hash256? Address => address;

    public virtual TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.GetOrLoadNode(address, in path, in hash, flags);

    public virtual bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.TryGetOrLoadNode(address, in path, in hash, out node, flags);

    public byte[]? LoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.LoadRlp(address, path, in hash, flags);

    public byte[]? TryLoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        fullTrieStore.TryLoadRlp(address, path, in hash, flags);

    public INodeStorage.KeyScheme Scheme => fullTrieStore.Scheme;

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address1) =>
        address1 == address ? this : WithAddress(address1);

    protected abstract ITrieNodeResolver WithAddress(Hash256? address1);
}
