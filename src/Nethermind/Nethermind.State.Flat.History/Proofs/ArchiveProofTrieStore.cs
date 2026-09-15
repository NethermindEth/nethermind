// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class ArchiveProofTrieStore(
    HistoricalTrieNodeBuilder builder,
    Func<ValueHash256, ITrieNodeResolver>? storageResolverFactory)
    : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        builder.LoadRlp(path, hash);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address is null) return this;
        if (storageResolverFactory is null)
        {
            throw new StateUnavailableException(
                "A storage trie was reached from a historical proof scope that serves the account trie only.");
        }

        return storageResolverFactory(new ValueHash256(address.Bytes));
    }
}
