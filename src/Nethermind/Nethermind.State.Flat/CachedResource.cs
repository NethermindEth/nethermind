// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public record CachedResource(
    ConcurrentDictionary<TreePath, TrieNode> TrieWarmerLoadedNodes,
    ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> LoadedStorageNodes
): IDisposable
{
    public BloomFilter PrewarmedAddresses = new BloomFilter(1_024, 12);

    public void Clear()
    {
        TrieWarmerLoadedNodes.NoResizeClear();
        LoadedStorageNodes.NoResizeClear();

        if (PrewarmedAddresses.Count > PrewarmedAddresses.Capacity)
        {
            long newCapacity = (long)BitOperations.RoundUpToPowerOf2((ulong)PrewarmedAddresses.Count);
            PrewarmedAddresses.Dispose();
            PrewarmedAddresses = new BloomFilter(newCapacity, PrewarmedAddresses.BitsPerKey);
        }
        else
        {
            PrewarmedAddresses.Clear();
        }
    }

    public bool ShouldPrewarm(Address address, UInt256? slot)
    {
        ulong hash;
        if (slot is null)
        {
            hash = XxHash64.HashToUInt64(address.Bytes);
        }
        else
        {
            Span<byte> buffer = stackalloc byte[20 + 32];
            address.Bytes.CopyTo(buffer);
            slot.Value.ToBigEndian(buffer[20..]);
            hash = XxHash64.HashToUInt64(buffer);
        }

        if (PrewarmedAddresses.MightContain(hash)) return false;
        PrewarmedAddresses.Add(hash);
        return true;
    }

    public void Dispose()
    {
        PrewarmedAddresses.Dispose();
    }
}
