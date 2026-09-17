// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Retains immutable node groups by canonical path and their logical subtree hash.</summary>
/// <remarks>
/// Each partition is split into hash-selected shards holding set-associative slots with second-chance replacement.
/// A shard's slots are one inline table, allocated whole on its first admission; writers serialize on the shard lock.
/// Lookups take no lock: each slot carries a seqlock version that is odd while a writer changes it, and a hit
/// leases the payload first and then re-reads the version, discarding the lease if the slot moved underneath it.
/// Account and code entries key on the narrower <see cref="PbtNodePath"/>; only the storage partition pays for <see cref="PbtStorageNodePath"/>.
/// </remarks>
public sealed class PbtTrieNodeCache(IPbtConfig config) : IDisposable
{
    private const int ShardCount = 256;
    private const int WaysPerSet = 8;
    // The payload wrapper object and the copied array's header; the slot itself is part of the table size.
    private const long EntryOverhead = 80;
    // Sizes the slot table only; a small assumption over-provisions slots rather than starving the byte budget.
    private const long AssumedPayloadSize = 512;
    private readonly Partition<PbtNodePath> _account = new(config.AccountTrieNodeCacheSizeBudget, "account");
    private readonly Partition<PbtNodePath> _code = new(config.CodeTrieNodeCacheSizeBudget, "code");
    private readonly Partition<PbtStorageNodePath> _storage = new(config.StorageTrieNodeCacheSizeBudget, "storage");
    private long _memorySize;
    private bool _disposed;

    internal long MemorySize => Interlocked.Read(ref _memorySize);

    private static bool IsStorage<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath> =>
        path.BitDepth == 4 && path.GetByte(0) == 0xF0
        || path.BitDepth >= 8 && path.GetByte(0) == Eip8297KeyDerivation.StorageZone;

    private static bool IsCode<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath> =>
        path.BitDepth >= 8 && path.GetByte(0) == Eip8297KeyDerivation.CodeZone;

    private static int ShardIndex(int hash) => (int)((uint)hash >> 24);

    private static long EntrySize(RefCountingMemory payload) => payload.GetSpan().Length + EntryOverhead;

    internal bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> =>
        IsStorage(path)
            ? TryGet(_storage, groupHash, path, out payload)
            : TryGet(IsCode(path) ? _code : _account, groupHash, path, out payload);

    private static bool TryGet<TPath, TStored>(Partition<TStored> partition, in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload)
        where TPath : struct, IPbtNodePath<TPath>
        where TStored : struct, IPbtNodePath<TStored>
    {
        int hash = path.GetHashCode();
        Entry<TStored>[]? entries = Volatile.Read(ref partition.Shards[ShardIndex(hash)].Entries);
        if (entries is not null)
        {
            int first = partition.SetIndex(hash) * WaysPerSet;
            for (int slot = first; slot < first + WaysPerSet; slot++)
            {
                ref Entry<TStored> entry = ref entries[slot];
                int version = Volatile.Read(ref entry.Version);
                RefCountingMemory? candidate = entry.Payload;
                if ((version & 1) != 0 || candidate is null || entry.GroupHash != groupHash || !entry.Path.Equals(path)) continue;
                if (!candidate.TryAcquireLease()) continue;
                // The lease's interlocked acquire fences the field reads above, so an unchanged version proves no writer touched the slot while they were read.
                if (Volatile.Read(ref entry.Version) == version)
                {
                    entry.Referenced = true;
                    payload = candidate;
                    Metrics.PbtTrieCacheHits.Increment(partition.Label);
                    return true;
                }
                ((IDisposable)candidate).Dispose();
            }
        }
        payload = null;
        Metrics.PbtTrieCacheMisses.Increment(partition.Label);
        return false;
    }

    internal void Add<TPath>(in ValueHash256 groupHash, TPath path, RefCountingMemory payload) where TPath : struct, IPbtNodePath<TPath>
    {
        if (IsStorage(path)) Add(_storage, groupHash, path, payload);
        else Add(IsCode(path) ? _code : _account, groupHash, path, payload);
    }

    private void Add<TPath, TStored>(Partition<TStored> partition, in ValueHash256 groupHash, TPath path, RefCountingMemory payload)
        where TPath : struct, IPbtNodePath<TPath>
        where TStored : struct, IPbtNodePath<TStored>
    {
        long size = EntrySize(payload);
        if ((ulong)(size + partition.TableSize) > partition.ShardBudget) return;
        int hash = path.GetHashCode();
        Shard<TStored> shard = partition.Shards[ShardIndex(hash)];
        lock (shard.Sync)
        {
            if (Volatile.Read(ref _disposed)) return;
            if (shard.Entries is null)
            {
                shard.Entries = new Entry<TStored>[partition.SetCount * WaysPerSet];
                shard.Hands = new byte[partition.SetCount];
                ChangeSize(partition, shard, partition.TableSize);
            }
            int set = partition.SetIndex(hash);
            int first = set * WaysPerSet;
            int target = -1;
            int stale = -1;
            for (int slot = first; slot < first + WaysPerSet; slot++)
            {
                ref Entry<TStored> entry = ref shard.Entries[slot];
                if (entry.Payload is null) target = target < 0 ? slot : target;
                else if (entry.Path.Equals(path))
                {
                    if (entry.GroupHash == groupHash) return;
                    stale = slot;
                }
            }
            // A newer subtree hash supersedes the same path's older group, so replace it before spending a free way.
            if (stale >= 0) target = stale;
            else if (target < 0) target = ClockVictim(shard, set, -1);
            if (shard.Entries[target].Payload is not null) Evict(partition, shard, target);
            while ((ulong)(shard.MemorySize + size) > partition.ShardBudget)
            {
                int victim = ClockVictim(shard, set, target);
                if (victim < 0) return;
                Evict(partition, shard, victim);
            }
            ref Entry<TStored> admitted = ref shard.Entries[target];
            Interlocked.Increment(ref admitted.Version);
            admitted.GroupHash = groupHash;
            admitted.Path = path.ToPath<TStored>();
            // Copy only on admission: a shrunk pooled payload may retain far more memory than its visible length.
            admitted.Payload = RefCountingMemory.Wrapping(payload.GetSpan().ToArray());
            admitted.Referenced = false;
            Volatile.Write(ref admitted.Version, admitted.Version + 1);
            ChangeSize(partition, shard, size);
        }
    }

    /// <summary>Second-chance sweep of one set: returns the first unreferenced slot other than <paramref name="skip"/>, clearing the flags it passes, or -1 when there is none.</summary>
    private static int ClockVictim<TStored>(Shard<TStored> shard, int set, int skip) where TStored : struct, IPbtNodePath<TStored>
    {
        int first = set * WaysPerSet;
        int hand = shard.Hands![set];
        for (int step = 0; step < 2 * WaysPerSet; step++)
        {
            int slot = first + hand;
            hand = (hand + 1) % WaysPerSet;
            ref Entry<TStored> entry = ref shard.Entries![slot];
            if (entry.Payload is null || slot == skip) continue;
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

    private void Evict<TStored>(Partition partition, Shard<TStored> shard, int slot) where TStored : struct, IPbtNodePath<TStored>
    {
        ref Entry<TStored> entry = ref shard.Entries![slot];
        RefCountingMemory payload = entry.Payload!;
        Interlocked.Increment(ref entry.Version);
        entry.Payload = null;
        Volatile.Write(ref entry.Version, entry.Version + 1);
        ChangeSize(partition, shard, -EntrySize(payload));
        ((IDisposable)payload).Dispose();
    }

    /// <summary>Releases retained groups without invalidating caller-owned leases.</summary>
    public void Clear()
    {
        Clear(_account);
        Clear(_code);
        Clear(_storage);
    }

    private void Clear<TStored>(Partition<TStored> partition) where TStored : struct, IPbtNodePath<TStored>
    {
        foreach (Shard<TStored> shard in partition.Shards)
            lock (shard.Sync) ClearShard(partition, shard);
    }

    private void ClearShard<TStored>(Partition partition, Shard<TStored> shard) where TStored : struct, IPbtNodePath<TStored>
    {
        if (shard.Entries is null) return;
        foreach (ref Entry<TStored> entry in shard.Entries.AsSpan()) ((IDisposable?)entry.Payload)?.Dispose();
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

    private abstract class Partition(ulong budget, string label, int slotSize)
    {
        internal readonly ulong ShardBudget = budget / ShardCount;
        internal readonly int SetCount = (int)Math.Max(1, budget / ShardCount / (ulong)(WaysPerSet * (slotSize + EntryOverhead + AssumedPayloadSize)));
        internal readonly string Label = label;

        internal long TableSize => 24 + SetCount * WaysPerSet * (long)slotSize + 24 + SetCount;
        // The top hash byte selects the shard, so the set is drawn from the remaining bits.
        internal int SetIndex(int hash) => (hash & 0xFFFFFF) % SetCount;
    }

    private sealed class Partition<TStored>(ulong budget, string label) : Partition(budget, label, Unsafe.SizeOf<Entry<TStored>>()) where TStored : struct, IPbtNodePath<TStored>
    {
        internal readonly Shard<TStored>[] Shards = CreateShards();

        private static Shard<TStored>[] CreateShards()
        {
            Shard<TStored>[] shards = new Shard<TStored>[ShardCount];
            for (int index = 0; index < shards.Length; index++) shards[index] = new Shard<TStored>();
            return shards;
        }
    }

    private abstract class Shard
    {
        internal readonly Lock Sync = new();
        internal byte[]? Hands;
        internal long MemorySize;
    }

    private sealed class Shard<TStored> : Shard where TStored : struct, IPbtNodePath<TStored>
    {
        internal Entry<TStored>[]? Entries;
    }

    private struct Entry<TStored> where TStored : struct, IPbtNodePath<TStored>
    {
        // Odd while a writer holds the slot open; an empty slot has a null payload.
        internal int Version;
        internal ValueHash256 GroupHash;
        internal TStored Path;
        internal RefCountingMemory? Payload;
        internal bool Referenced;
    }
}
