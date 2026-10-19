// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>A node source over path-keyed node RLP, for tests and benchmarks: a fetched node is verified and inserted into <paramref name="warmed"/>, which serves it after.</summary>
/// <param name="address">The storage trie's address hash, or null for the state trie.</param>
internal sealed class DictionaryNodeSource(IReadOnlyDictionary<TreePath, byte[]> nodes, WarmedNodeLog warmed, Hash256? address) : INodeRecordSource
{
    private int _fetchCount;

    /// <summary>Number of lookups that missed the warmed nodes and went to the dictionary.</summary>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    public bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecord record)
    {
        if (warmed.TryGet(address, path, hash, out record, out _)) return true;

        byte[]? rlp = LoadRlp(path, hash);
        return rlp is not null && warmed.TryAppend(address, path, hash, rlp, out record, out _);
    }

    /// <param name="buffer">The buffer holding the trie, which the warmed nodes are combined with.</param>
    public void WarmUp(INodeRecordBuffer buffer, TrieRoot root, in ValueHash256 key, bool forDelete = false) =>
        BufferWarmUp.WarmUp(new WarmedNodeRecordBuffer(buffer, warmed), warmed, LoadRlp, address, root, key, forDelete);

    private void LoadRlp(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, BufferWarmUp.WarmUpRead> action, BufferWarmUp.WarmUpRead read)
    {
        byte[]? rlp = LoadRlp(path, hash);
        if (rlp is not null) action(rlp, read);
    }

    private byte[]? LoadRlp(in TreePath path, in ValueHash256 hash)
    {
        Interlocked.Increment(ref _fetchCount);
        return nodes.GetValueOrDefault(path);
    }
}
