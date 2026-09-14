// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Retains immutable node groups by canonical path and their logical subtree hash.</summary>
/// <remarks>
/// Each partition is split into hash-selected shards holding set-associative slots with second-chance replacement.
/// Each shard owns its payload references; a hit acquires a separate caller-owned reference under the shard lock.
/// </remarks>
public sealed class PbtTrieNodeCache(IPbtConfig config) : IDisposable
{
    private const int ShardCount = 256;
    private const int WaysPerSet = 8;
    private const long EntryOverhead = 384;
    // Sizes the slot table only; a slot is one reference, so a small assumption over-provisions slots rather than starving the byte budget.
    private const long AssumedEntrySize = EntryOverhead + 512;
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

    private static int ShardIndex(int hash) => (int)((uint)hash >> 24);

    internal bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        Partition partition = GetPartition(path);
        int hash = path.GetHashCode();
        Shard shard = partition.Shards[ShardIndex(hash)];
        lock (shard.Sync)
        {
            if (!Volatile.Read(ref _disposed) && shard.Entries is not null)
            {
                int first = partition.SetIndex(hash) * WaysPerSet;
                for (int slot = first; slot < first + WaysPerSet; slot++)
                {
                    Entry? entry = shard.Entries[slot];
                    if (entry is not null && entry.GroupHash == groupHash && entry.Path.Equals(path))
                    {
                        entry.Referenced = true;
                        entry.Payload.AcquireLease();
                        payload = entry.Payload;
                        Metrics.PbtTrieCacheHits.Increment(partition.Label);
                        return true;
                    }
                }
            }
        }
        payload = null;
        Metrics.PbtTrieCacheMisses.Increment(partition.Label);
        return false;
    }

    internal void Add<TPath>(in ValueHash256 groupHash, TPath path, RefCountingMemory payload) where TPath : struct, IPbtNodePath<TPath>
    {
        Partition partition = GetPartition(path);
        long size = payload.GetSpan().Length + EntryOverhead;
        if ((ulong)(size + partition.TableSize) > partition.ShardBudget) return;
        int hash = path.GetHashCode();
        Shard shard = partition.Shards[ShardIndex(hash)];
        lock (shard.Sync)
        {
            if (Volatile.Read(ref _disposed)) return;
            if (shard.Entries is null)
            {
                shard.Entries = new Entry[partition.SetCount * WaysPerSet];
                shard.Hands = new byte[partition.SetCount];
                ChangeSize(partition, shard, partition.TableSize);
            }
            int set = partition.SetIndex(hash);
            int first = set * WaysPerSet;
            int target = -1;
            int stale = -1;
            for (int slot = first; slot < first + WaysPerSet; slot++)
            {
                Entry? entry = shard.Entries[slot];
                if (entry is null) target = target < 0 ? slot : target;
                else if (entry.Path.Equals(path))
                {
                    if (entry.GroupHash == groupHash) return;
                    stale = slot;
                }
            }
            // A newer subtree hash supersedes the same path's older group, so replace it before spending a free way.
            if (stale >= 0) target = stale;
            else if (target < 0) target = ClockVictim(shard, set, -1);
            if (shard.Entries[target] is not null) Evict(partition, shard, target);
            while ((ulong)(shard.MemorySize + size) > partition.ShardBudget)
            {
                int victim = ClockVictim(shard, set, target);
                if (victim < 0) return;
                Evict(partition, shard, victim);
            }
            // Copy only on admission: a shrunk pooled payload may retain far more memory than its visible length.
            RefCountingMemory cachedPayload = RefCountingMemory.Wrapping(payload.GetSpan().ToArray());
            shard.Entries[target] = new Entry(groupHash, path.ToPath<PbtStorageNodePath>(), cachedPayload, size);
            ChangeSize(partition, shard, size);
        }
    }

    /// <summary>Second-chance sweep of one set: returns the first unreferenced slot other than <paramref name="skip"/>, clearing the flags it passes, or -1 when there is none.</summary>
    private static int ClockVictim(Shard shard, int set, int skip)
    {
        int first = set * WaysPerSet;
        int hand = shard.Hands![set];
        for (int step = 0; step < 2 * WaysPerSet; step++)
        {
            int slot = first + hand;
            hand = (hand + 1) % WaysPerSet;
            Entry? entry = shard.Entries![slot];
            if (entry is null || slot == skip) continue;
            if (entry.Referenced)
            {
                entry.Referenced = false;
                continue;
            }
            shard.Hands[set] = (byte)hand;
            return slot;
        }
        return -1;
    }

    private void Evict(Partition partition, Shard shard, int slot)
    {
        Entry entry = shard.Entries![slot]!;
        shard.Entries[slot] = null;
        ((IDisposable)entry.Payload).Dispose();
        ChangeSize(partition, shard, -entry.Size);
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
        shard.Hands = null;
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
        internal readonly int SetCount = (int)Math.Max(1, budget / ShardCount / (ulong)(WaysPerSet * AssumedEntrySize));
        internal readonly string Label = label;

        internal long TableSize => 24 + SetCount * WaysPerSet * 8L + 24 + SetCount;
        // The top hash byte selects the shard, so the set is drawn from the remaining bits.
        internal int SetIndex(int hash) => (hash & 0xFFFFFF) % SetCount;
    }

    private sealed class Shard
    {
        internal readonly Lock Sync = new();
        internal Entry?[]? Entries;
        internal byte[]? Hands;
        internal long MemorySize;
    }

    private sealed class Entry(ValueHash256 groupHash, PbtStorageNodePath path, RefCountingMemory payload, long size)
    {
        internal readonly ValueHash256 GroupHash = groupHash;
        internal readonly PbtStorageNodePath Path = path;
        internal readonly RefCountingMemory Payload = payload;
        internal readonly long Size = size;
        internal bool Referenced;
    }
}
