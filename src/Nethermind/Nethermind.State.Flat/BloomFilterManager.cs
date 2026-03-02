// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.IO.Hashing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public sealed class BloomFilterManager(int rangeSize, long estimatedEntriesPerBlock, double bitsPerKey)
    : IBloomFilterManager, IDisposable
{
    private readonly ConcurrentDictionary<long, BlockRangeBloomFilter> _blooms = new();

    public ArrayPoolList<IBloomFilter> GetBloomFiltersForRange(long startingBlockNumber, long endingBlockNumber)
    {
        long startIdx = startingBlockNumber / rangeSize;
        long endIdx = endingBlockNumber / rangeSize;

        ArrayPoolList<IBloomFilter> result = new((int)(endIdx - startIdx + 1));
        for (long i = startIdx; i <= endIdx; i++)
        {
            if (_blooms.TryGetValue(i, out BlockRangeBloomFilter? bloom))
            {
                result.Add(bloom);
            }
        }

        return result;
    }

    public void AddEntries(Snapshot snapshot)
    {
        long blockNumber = snapshot.To.BlockNumber;
        long idx = blockNumber / rangeSize;

        BlockRangeBloomFilter bloom = _blooms.GetOrAdd(idx, static (key, state) =>
        {
            long capacity = Math.Max(state.estimatedEntriesPerBlock * state.rangeSize, 64);
            BloomFilter inner = new(capacity, state.bitsPerKey);
            return new BlockRangeBloomFilter(inner, key * state.rangeSize, (key + 1) * state.rangeSize - 1);
        }, (rangeSize, estimatedEntriesPerBlock, bitsPerKey));

        foreach (AddressAsKey key in snapshot.Content.Accounts.Keys)
            bloom.Add(XxHash64.HashToUInt64(((Address)key).Bytes));

        foreach (AddressAsKey key in snapshot.Content.SelfDestructedStorageAddresses.Keys)
            bloom.Add(XxHash64.HashToUInt64(((Address)key).Bytes));

        foreach ((AddressAsKey addr, UInt256 _) in snapshot.Content.Storages.Keys)
            bloom.Add(XxHash64.HashToUInt64(((Address)addr).Bytes));

        foreach ((Hash256AsKey addr, TreePath _) in snapshot.Content.StorageNodes.Keys)
            bloom.Add(XxHash64.HashToUInt64(((Hash256)addr).Bytes));
    }

    public void RemoveBloomsUpTo(long blockNumber)
    {
        foreach (KeyValuePair<long, BlockRangeBloomFilter> kv in _blooms)
        {
            if (kv.Value.EndingBlockNumber <= blockNumber && _blooms.TryRemove(kv.Key, out BlockRangeBloomFilter? bloom))
            {
                bloom.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (BlockRangeBloomFilter bloom in _blooms.Values)
            bloom.Dispose();
        _blooms.Clear();
    }
}
