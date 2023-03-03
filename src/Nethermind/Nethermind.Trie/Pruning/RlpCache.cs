// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class RlpCache
{
    private readonly int _size;
    private const int DefaultRlpCacheSize = 2048;
    private readonly RlpCacheItem[] _items;
    private readonly BitArray _rlpCacheSet;

    public RlpCache(int size = DefaultRlpCacheSize)
    {
        _size = size;
        _items = new RlpCacheItem[size];
        _rlpCacheSet = new BitArray(size);
    }

    private record RlpCacheItem(Keccak Keccak, byte[] Rlp);

    /// <summary>
    /// Tries to store the rlp cache.
    /// </summary>
    /// <param name="node"></param>
    public void SetRlpCache(TrieNode node)
    {
        if (node.CacheHint is SearchHint.StorageRoot or SearchHint.StorageChildNode)
        {
            int bucket = GetRlpCacheBucket(node.Keccak!);

            Metrics.RlpCacheWriteAttempts++;

            if (_rlpCacheSet.Get(bucket) == false)
            {
                // nothing written this round, write and set
                Volatile.Write(ref _items[bucket], new RlpCacheItem(node.Keccak, node.FullRlp!));

                Metrics.RlpCacheWrites++;

                _rlpCacheSet.Set(bucket, true);
            }
        }
    }

    /// <summary>
    /// Reads through the Rlp cache.
    /// </summary>
    /// <param name="keccak"></param>
    /// <returns></returns>
    public byte[]? GetRlpCache(Keccak keccak)
    {
        RlpCacheItem item = Volatile.Read(ref _items[GetRlpCacheBucket(keccak)]);
        if (item != null && item.Keccak == keccak)
        {
            Metrics.RlpCacheHit++;
            return item.Rlp;
        }

        Metrics.RlpCacheMiss++;
        return default;
    }

    private int GetRlpCacheBucket(Keccak keccak) => (int)(MemoryMarshal.Read<uint>(keccak.Bytes) % _size);

    /// <summary>
    /// Clears the set flags, so that all the cache items can be written again.
    /// </summary>
    public void ClearSetFlags() => _rlpCacheSet.SetAll(false);

    public void Clear()
    {
        Array.Clear(_items);
        ClearSetFlags();
    }
}
