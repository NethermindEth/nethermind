// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
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
    private readonly Shard[] _shards = CreateShards();
    private readonly ulong _shardBudget = config.TrieCacheMemoryBudget / ShardCount;
    private long _memorySize;
    private bool _disposed;

    internal long MemorySize => Interlocked.Read(ref _memorySize);

    private static Shard[] CreateShards()
    {
        Shard[] shards = new Shard[ShardCount];
        for (int index = 0; index < shards.Length; index++) shards[index] = new Shard();
        return shards;
    }

    private static int ShardIndex(IPbtNodePath path) => path.BitDepth > 8
        ? (path.Path[0] + path.Path[1]) & (ShardCount - 1)
        : path.Path.IsEmpty ? 0 : path.Path[0];
    private static int BucketIndex(IPbtNodePath path) => path.GetHashCode() & (BucketCount - 1);

    internal bool TryGet(in ValueHash256 root, IPbtNodePath path, [NotNullWhen(true)] out RefCountingMemory? payload)
    {
        Shard shard = _shards[ShardIndex(path)];
        lock (shard.Sync)
        {
            Entry? entry = shard.Entries?[BucketIndex(path)];
            if (!Volatile.Read(ref _disposed) && entry is not null && entry.Root == root
                && entry.Path.Equals(path))
            {
                entry.Payload.AcquireLease();
                payload = entry.Payload;
                return true;
            }
        }
        payload = null;
        return false;
    }

    internal void Add(in ValueHash256 root, IPbtNodePath path, RefCountingMemory payload)
    {
        long size = payload.GetSpan().Length + EntryOverhead;
        if ((ulong)(size + TableSize) > _shardBudget) return;
        Shard shard = _shards[ShardIndex(path)];
        lock (shard.Sync)
        {
            if (Volatile.Read(ref _disposed)) return;
            int bucket = BucketIndex(path);
            Entry? previous = shard.Entries?[bucket];
            if (previous is not null && previous.Root == root && previous.Path.Equals(path)) return;
            if ((ulong)(shard.MemorySize + size) > _shardBudget)
            {
                ClearShard(shard);
                previous = null;
            }
            if (shard.Entries is null)
            {
                shard.Entries = new Entry[BucketCount];
                ChangeSize(shard, TableSize);
            }
            // Copy only on admission: a shrunk pooled payload may retain far more memory than its visible length.
            RefCountingMemory cachedPayload = RefCountingMemory.Wrapping(payload.GetSpan().ToArray());
            shard.Entries[bucket] = new Entry(root, path, cachedPayload, size);
            ChangeSize(shard, size);
            if (previous is not null)
            {
                ((IDisposable)previous.Payload).Dispose();
                ChangeSize(shard, -previous.Size);
            }
        }
    }

    /// <summary>Releases retained groups without invalidating caller-owned leases.</summary>
    public void Clear()
    {
        foreach (Shard shard in _shards)
            lock (shard.Sync) ClearShard(shard);
    }

    private void ClearShard(Shard shard)
    {
        if (shard.Entries is null) return;
        foreach (Entry? entry in shard.Entries) ((IDisposable?)entry?.Payload)?.Dispose();
        shard.Entries = null;
        ChangeSize(shard, -shard.MemorySize);
    }

    private void ChangeSize(Shard shard, long delta)
    {
        shard.MemorySize += delta;
        Interlocked.Add(ref _memorySize, delta);
        Interlocked.Add(ref Metrics.PbtTrieCacheMemory, delta);
    }

    /// <summary>Stops admission and releases all cache-owned payload references.</summary>
    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        Clear();
    }

    private sealed class Shard
    {
        internal readonly Lock Sync = new();
        internal Entry?[]? Entries;
        internal long MemorySize;
    }

    private sealed record Entry(ValueHash256 Root, IPbtNodePath Path, RefCountingMemory Payload, long Size);
}
