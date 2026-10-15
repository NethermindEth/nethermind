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
/// so a collision replaces the older record and a lookup checks the record's hash. A record holds its node key but not its path,
/// so the shard is picked by node key bits that neither the slot index nor the signature of a shard's table uses.
/// A shard whose arena is full is replaced by an empty one instead of being reset: its arrays are never reused, so a reader
/// still holding one of its records keeps reading the same bytes. A budget of zero caches nothing.
/// </remarks>
public sealed class TrieNodeCache(IFlatDbConfig flatDbConfig) : ITrieNodeCache
{
    private const int ShardCount = 256;
    private const int ShardKeyShift = 24;
    private const int AverageRecordBytes = 350;
    private const double UtilRatio = 0.25;
    private const int MinSlotsPerShard = 16;

    private readonly RecordTable?[] _shards = new RecordTable?[ShardCount];
    private readonly long _shardMemoryBudget = (long)(flatDbConfig.TrieCacheMemoryBudget / ShardCount);
    private readonly int _slotsPerShard = (int)BitOperations.RoundUpToPowerOf2(
        (uint)Math.Max(MinSlotsPerShard, flatDbConfig.TrieCacheMemoryBudget / AverageRecordBytes / UtilRatio / ShardCount));

    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        RecordTable? shard = Volatile.Read(ref _shards[ShardOf(NodeRecord.NodeKey(address, path))]);
        if (shard is not null) return shard.TryGet(address, path, hash, out record);

        record = default;
        return false;
    }

    /// <summary>Copies the records warmed by a retired block into the cache, once no warm-up can still be writing them.</summary>
    public void Add(TransientResource transientResource)
    {
        transientResource.WaitForExclusiveLease();
        if (_shardMemoryBudget == 0) return;

        using ArrayPoolList<NodeRecordRef> warmed = new(transientResource.WarmedNodes.Count);
        transientResource.WarmedNodes.CopyAllTo(warmed);
        if (warmed.Count == 0) return;

        int[] shardStarts = new int[ShardCount + 1];
        foreach (NodeRecordRef record in warmed.AsSpan()) shardStarts[ShardOf(record) + 1]++;
        for (int shard = 1; shard <= ShardCount; shard++) shardStarts[shard] += shardStarts[shard - 1];

        int[] nextPositions = shardStarts[..ShardCount];
        using ArrayPoolList<NodeRecordRef> byShard = new(warmed.Count, warmed.Count);
        foreach (NodeRecordRef record in warmed.AsSpan()) byShard[nextPositions[ShardOf(record)]++] = record;

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

    private static int ShardOf(ulong nodeKey) => (int)(nodeKey >> ShardKeyShift) & (ShardCount - 1);

    private static int ShardOf(in NodeRecordRef record) => ShardOf(record.View.NodeKey);

    private void AddToShard(int shardIndex, ReadOnlySpan<NodeRecordRef> records)
    {
        RecordTable shard = _shards[shardIndex] ?? ReplaceShard(shardIndex);
        foreach (NodeRecordRef record in records)
        {
            // A record that does not fit an empty shard is skipped rather than replacing the shard for nothing.
            if (shard.TryAdd(record.Span, out _) || shard.ArenaBytes == 0) continue;

            shard = ReplaceShard(shardIndex);
            shard.TryAdd(record.Span, out _);
        }
    }

    private RecordTable ReplaceShard(int shardIndex)
    {
        RecordTable shard = new(_slotsPerShard, _shardMemoryBudget);
        Volatile.Write(ref _shards[shardIndex], shard);
        return shard;
    }
}
