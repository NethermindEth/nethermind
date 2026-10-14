// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <summary>A node source over path-keyed node RLP, for tests and benchmarks: a fetched node is verified and inserted into <paramref name="warmed"/>, which serves it after.</summary>
/// <param name="address">The storage trie's address hash, or null for the state trie.</param>
internal sealed class DictionaryNodeSource(IReadOnlyDictionary<TreePath, byte[]> nodes, WarmedNodes warmed, Hash256? address) : INodeRecordSource
{
    private int _fetchCount;

    /// <summary>Number of lookups that missed the warmed nodes and went to the dictionary.</summary>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    public bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        if (warmed.TryGet(address, path, hash, out record)) return true;

        Interlocked.Increment(ref _fetchCount);
        return nodes.TryGetValue(path, out byte[]? rlp) && warmed.TryInsert(address, path, hash, rlp, out record);
    }
}
