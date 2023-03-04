// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie.Pruning;

public class RlpCache
{
    private readonly int _size;
    private const int DefaultRlpCacheSize = 4096;
    private readonly RlpCacheItem[] _items;
    private readonly BitArray _rlpCacheSet;

    private const bool IsSet = true;

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
        if (ShouldCache(node))
        {
            int bucket = GetRlpCacheBucket(node.Keccak!);

            Metrics.RlpCacheWriteAttempts++;

            if (_rlpCacheSet.Get(bucket) != IsSet)
            {
                // nothing written this round, write and set
                Volatile.Write(ref _items[bucket], new RlpCacheItem(node.Keccak, node.FullRlp!));

                Metrics.RlpCacheWrites++;

                _rlpCacheSet.Set(bucket, IsSet);
            }
        }
    }

    /// <summary>
    /// Provides a heuristic whether the given node should be tried to have its rlp cached.
    /// </summary>
    private static bool ShouldCache(TrieNode node)
    {
        // cache only a storage branch that has 3+ children
        return node.IsBranch
               && (node.CacheHint == SearchHint.StorageRoot ||
                   node.CacheHint == SearchHint.StorageChildNode)
               && node.IsValidWithOneNodeLess;
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
            Metrics.RlpCacheHits++;
            return item.Rlp;
        }

        return default;
    }

    /// <summary>
    /// Gets the bucket of the given keccak.
    /// </summary>
    /// <remarks>
    /// The last 4 ints are used so that the comparison can faster fail.
    /// </remarks>
    private int GetRlpCacheBucket(Keccak keccak) =>
        (int)(MemoryMarshal.Read<uint>(keccak.Bytes.Slice(Keccak.Size - sizeof(uint))) % _size);

    /// <summary>
    /// Clears the set flags, so that all the cache items can be written again.
    /// </summary>
    public void ClearSetFlags() => _rlpCacheSet.SetAll(!IsSet);

    public void Clear()
    {
        Array.Clear(_items);
        ClearSetFlags();
    }
}
