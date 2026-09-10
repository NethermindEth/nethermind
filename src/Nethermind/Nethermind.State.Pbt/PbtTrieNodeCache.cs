// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Retains immutable node groups by canonical path and the PBT root of their read-only view.</summary>
/// <remarks>Each shard owns its payload references; a hit acquires a separate caller-owned reference under the shard lock.</remarks>
public sealed class PbtTrieNodeCache(IPbtConfig config) : IDisposable
{
    private const int ShardCount = 256;
    private const int BucketCount = 256;
    private const long TableSize = 24 + BucketCount * 8;
    private const long EntryOverhead = 384;
    private readonly Partition[] _partitions =
    [
        new(config.AccountTrieNodeCacheSizeBudget, "account"),
        new(config.CodeTrieNodeCacheSizeBudget, "code"),
        new(config.StorageTrieNodeCacheSizeBudget, "storage"),
    ];
    private long _memorySize;
    private bool _disposed;

    internal long MemorySize => Interlocked.Read(ref _memorySize);

    private static Shard[] CreateShards()
    {
        Shard[] shards = new Shard[ShardCount];
        for (int index = 0; index < shards.Length; index++) shards[index] = new Shard();
        return shards;
    }

    private Partition GetPartition<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        if (path.BitDepth == 0) return _partitions[0];
        if (path.BitDepth == 4 && path.GetByte(0) == 0xF0
            || path.BitDepth >= 8 && path.GetByte(0) == Eip8297KeyDerivation.StorageZone)
            return _partitions[2];
        return _partitions[path.BitDepth >= 8 && path.GetByte(0) == Eip8297KeyDerivation.CodeZone ? 1 : 0];
    }

    private static int ShardIndex<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath> => path.BitDepth > 8
        ? path.GetByte(1)
        : 0;
    private static int BucketIndex<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath> => path.GetHashCode() & (BucketCount - 1);

    internal bool TryGet<TPath>(in ValueHash256 root, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        Partition partition = GetPartition(path);
        Shard shard = partition.Shards[ShardIndex(path)];
        lock (shard.Sync)
        {
            Entry? entry = shard.Entries?[BucketIndex(path)];
            if (!Volatile.Read(ref _disposed) && entry is not null && entry.Root == root
                && entry.Path.Equals(path))
            {
                entry.Payload.AcquireLease();
                payload = entry.Payload;
                Metrics.PbtTrieCacheHits.Increment(partition.Label);
                return true;
            }
        }
        payload = null;
        Metrics.PbtTrieCacheMisses.Increment(partition.Label);
        return false;
    }

    internal void Add<TPath>(in ValueHash256 root, TPath path, RefCountingMemory payload) where TPath : struct, IPbtNodePath<TPath>
    {
        Partition partition = GetPartition(path);
        long size = payload.GetSpan().Length + EntryOverhead;
        if ((ulong)(size + TableSize) > partition.ShardBudget) return;
        Shard shard = partition.Shards[ShardIndex(path)];
        lock (shard.Sync)
        {
            if (Volatile.Read(ref _disposed)) return;
            int bucket = BucketIndex(path);
            Entry? previous = shard.Entries?[bucket];
            if (previous is not null && previous.Root == root && previous.Path.Equals(path)) return;
            if ((ulong)(shard.MemorySize + size) > partition.ShardBudget)
            {
                ClearShard(partition, shard);
                previous = null;
            }
            if (shard.Entries is null)
            {
                shard.Entries = new Entry[BucketCount];
                ChangeSize(partition, shard, TableSize);
            }
            // Copy only on admission: a shrunk pooled payload may retain far more memory than its visible length.
            RefCountingMemory cachedPayload = RefCountingMemory.Wrapping(payload.GetSpan().ToArray());
            shard.Entries[bucket] = new Entry(root, path.ToPath<PbtStorageNodePath>(), cachedPayload, size);
            ChangeSize(partition, shard, size);
            if (previous is not null)
            {
                ((IDisposable)previous.Payload).Dispose();
                ChangeSize(partition, shard, -previous.Size);
            }
        }
    }

    /// <summary>Releases retained groups without invalidating caller-owned leases.</summary>
    public void Clear()
    {
        foreach (Partition partition in _partitions)
            foreach (Shard shard in partition.Shards)
                lock (shard.Sync) ClearShard(partition, shard);
    }

    private void ClearShard(Partition partition, Shard shard)
    {
        if (shard.Entries is null) return;
        foreach (Entry? entry in shard.Entries) ((IDisposable?)entry?.Payload)?.Dispose();
        shard.Entries = null;
        ChangeSize(partition, shard, -shard.MemorySize);
    }

    private void ChangeSize(Partition partition, Shard shard, long delta)
    {
        shard.MemorySize += delta;
        Interlocked.Add(ref _memorySize, delta);
        Metrics.PbtTrieCacheMemory.AddBy(partition.Label, delta);
    }

    /// <summary>Stops admission and releases all cache-owned payload references.</summary>
    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        Clear();
    }

    private sealed class Partition(ulong budget, string label)
    {
        internal readonly Shard[] Shards = CreateShards();
        internal readonly ulong ShardBudget = budget / ShardCount;
        internal readonly string Label = label;
    }

    private sealed class Shard
    {
        internal readonly Lock Sync = new();
        internal Entry?[]? Entries;
        internal long MemorySize;
    }

    private sealed record Entry(ValueHash256 Root, PbtStorageNodePath Path, RefCountingMemory Payload, long Size);
}
