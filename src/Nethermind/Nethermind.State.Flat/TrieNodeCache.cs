// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;

namespace Nethermind.State.Flat;

/// <summary>
/// A cache of the trie nodes warmed by earlier blocks, as hash-verified node records keyed by node position.
/// </summary>
/// <remarks>
/// The records live in 256 shards, each a direct-mapped <see cref="RecordTable"/> holding an equal share of the memory budget,
/// so a collision replaces the older record and a lookup checks the record's hash. The shard is picked by the first path byte
/// plus the first address byte, so the nodes of one subtree, which tend to be read together, share a shard, while the storage
/// trie roots spread over the shards.
/// A shard whose arena is full is replaced by an empty one instead of being reset: its arrays are never reused, so a reader
/// still holding one of its records keeps reading the same bytes. A budget of zero caches nothing.
/// </remarks>
public sealed class TrieNodeCache(IFlatDbConfig flatDbConfig) : ITrieNodeCache
{
    private const int ShardCount = 256;
    private const int AverageRecordBytes = 350;
    private const double UtilRatio = 0.25;
    private const int MinSlotsPerShard = 16;

    private readonly RecordTable?[] _shards = new RecordTable?[ShardCount];
    private readonly long _shardMemoryBudget = (long)(flatDbConfig.TrieCacheMemoryBudget / ShardCount);
    private readonly int _slotsPerShard = (int)BitOperations.RoundUpToPowerOf2(
        (uint)Math.Max(MinSlotsPerShard, flatDbConfig.TrieCacheMemoryBudget / AverageRecordBytes / UtilRatio / ShardCount));

    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        RecordTable? shard = Volatile.Read(ref _shards[ShardOf(address, path)]);
        if (shard is not null) return shard.TryGet(address, path, hash, out record);

        record = default;
        return false;
    }

    /// <summary>Copies the records warmed by a retired block into the cache, once no warm-up can still be writing them.</summary>
    public void Add(TransientResource transientResource)
    {
        transientResource.WaitForExclusiveLease();
        if (_shardMemoryBudget == 0) return;

        using ArrayPoolList<(Hash256? Address, TreePath Path, NodeRecordRef Record)> warmed = new(transientResource.WarmedNodes.Count);
        transientResource.WarmedNodes.CopyAllTo(warmed);
        if (warmed.Count == 0) return;

        int[] shardStarts = new int[ShardCount + 1];
        foreach ((Hash256? address, TreePath path, _) in warmed.AsSpan()) shardStarts[ShardOf(address, path) + 1]++;
        for (int shard = 1; shard <= ShardCount; shard++) shardStarts[shard] += shardStarts[shard - 1];

        int[] nextPositions = shardStarts[..ShardCount];
        using ArrayPoolList<(Hash256? Address, TreePath Path, NodeRecordRef Record)> byShard = new(warmed.Count, warmed.Count);
        foreach ((Hash256? Address, TreePath Path, NodeRecordRef Record) warmedRecord in warmed.AsSpan())
        {
            byShard[nextPositions[ShardOf(warmedRecord.Address, warmedRecord.Path)]++] = warmedRecord;
        }

        Parallel.For(0, ShardCount, shard => AddToShard(shard, byShard.AsSpan()[shardStarts[shard]..shardStarts[shard + 1]]));

        long memoryUsed = 0;
        foreach (RecordTable? shard in _shards) memoryUsed += shard?.ArenaBytes ?? 0;
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = memoryUsed;
    }

    /// <summary>
    /// Clears all cached trie nodes.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_shards);
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = 0;
    }

    private static int ShardOf(Hash256? address, in TreePath path) => (path.Path.Bytes[0] + (address?.Bytes[0] ?? 0)) % ShardCount;

    private void AddToShard(int shardIndex, ReadOnlySpan<(Hash256? Address, TreePath Path, NodeRecordRef Record)> records)
    {
        if (records.IsEmpty) return;

        RecordTable? earlierShard = _shards[shardIndex];
        RecordTable shard = earlierShard ?? ReplaceShard(shardIndex);
        for (int i = 0; i < records.Length; i++)
        {
            (Hash256? address, TreePath path, NodeRecordRef record) = records[i];
            // A record that does not fit an empty shard is skipped rather than replacing the shard for nothing.
            if (shard.TryAdd(address, path, record.Span, out _) || shard.ArenaBytes == 0) continue;

            // The records an earlier block's shard already held, such as cache hits, were not copied, so they are added
            // again from the start of the batch; a shard this batch filled itself only retries the record.
            i = shard == earlierShard ? -1 : i - 1;
            shard = ReplaceShard(shardIndex);
        }
    }

    private RecordTable ReplaceShard(int shardIndex)
    {
        RecordTable shard = new(_slotsPerShard, _shardMemoryBudget);
        Volatile.Write(ref _shards[shardIndex], shard);
        return shard;
    }
}
