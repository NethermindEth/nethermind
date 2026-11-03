// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Migration;

/// <summary>Resolves the nodes of a state or storage trie through a persistence reader's hash-taking reads, without caching.</summary>
/// <param name="reader">The reader to load nodes from.</param>
/// <param name="address">The account path of the storage trie, or <c>null</c> for the state trie.</param>
internal sealed class ReaderTrieStore(IPersistence.IPersistenceReader reader, Hash256? address) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        address is null
            ? reader.TryLoadStateRlp(path, hash.ValueHash256, flags)
            : reader.TryLoadStorageRlp(address, path, hash.ValueHash256, flags);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? storageAddress) =>
        storageAddress is null ? this : new ReaderTrieStore(reader, storageAddress);
}
