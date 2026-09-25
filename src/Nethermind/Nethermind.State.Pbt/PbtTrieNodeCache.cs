// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Retains immutable node groups by canonical path and their logical subtree hash.</summary>
/// <remarks>
/// Each partition is split into hash-selected shards holding set-associative slots with second-chance replacement.
/// A shard's slots are one inline table allocated up front, sized from the partition budget; each shard has one writer at a
/// time, since ingestion hands every shard to a single worker and nothing else writes concurrently with it.
/// Lookups take no lock: each slot carries a seqlock version that is odd while a writer changes it, and a hit
/// leases the payload first and then re-reads the version, discarding the lease if the slot moved underneath it.
/// Account and code entries key on the narrower <see cref="PbtNodePath"/>; only the storage partition pays for <see cref="PbtStorageNodePath"/>.
/// </remarks>
public sealed class PbtTrieNodeCache(IPbtConfig config) : IPbtTrieNodeCache, IDisposable
{
    private const int ShardCount = 256;
    private const int WaysPerSet = 8;
    // The payload wrapper object and its array header; the slot itself is part of the preallocated table.
    private const long EntryOverhead = 80;
    // Sizes the slot table only; a small assumption over-provisions slots rather than starving the byte budget.
    private const long AssumedPayloadSize = 512;
    private readonly Partition<PbtNodePath> _account = new(config.AccountTrieNodeCacheSizeBudget, "account");
    private readonly Partition<PbtNodePath> _code = new(config.CodeTrieNodeCacheSizeBudget, "code");
    private readonly Partition<PbtStorageNodePath> _storage = new(config.StorageTrieNodeCacheSizeBudget, "storage");

    /// <summary>Bytes retained for payloads; the slot tables are fixed by the configured budgets and not counted.</summary>
    internal long MemorySize => _account.MemorySize + _code.MemorySize + _storage.MemorySize;

    internal long EntryCount => _account.EntryCount + _code.EntryCount + _storage.EntryCount;

    private static bool IsStorage<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath> =>
        path.BitDepth == 4 && path.GetByte(0) == 0xF0
        || path.BitDepth >= 8 && path.GetByte(0) == Eip8297KeyDerivation.StorageZone;

    private static bool IsCode<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath> =>
        path.BitDepth >= 8 && path.GetByte(0) == Eip8297KeyDerivation.CodeZone;

    /// <summary>Leases the retained group at <paramref name="path"/> whose subtree hash is <paramref name="groupHash"/>.</summary>
    /// <returns><c>true</c> when <paramref name="payload"/> holds a caller-owned lease to release with <see cref="IDisposable.Dispose"/>.</returns>
    public bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> =>
        IsStorage(path)
            ? TryGet(_storage, groupHash, path, out payload)
            : TryGet(IsCode(path) ? _code : _account, groupHash, path, out payload);

    private static bool TryGet<TPath, TStored>(Partition<TStored> partition, in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload)
        where TPath : struct, IPbtNodePath<TPath>
        where TStored : struct, IPbtNodePath<TStored>
    {
        bool hit = partition.TryGet(groupHash, path, out payload);
        (hit ? Metrics.PbtTrieCacheHits : Metrics.PbtTrieCacheMisses).Increment(partition.Label);
        return hit;
    }

    /// <summary>Folds a retired block's staged groups into the shared cache once its last reader has left.</summary>
    /// <remarks>
    /// Child shard <c>i</c> maps onto parent shard <c>i</c>, so the shards ingest in parallel with one writer each.
    /// Must not run concurrently with another write to the cache.
    /// </remarks>
    public void Add(PbtTransientResource transientResource)
    {
        transientResource.WaitForExclusiveLease();
        ChildCache child = transientResource.NodeGroups;
        long account = 0;
        long code = 0;
        long storage = 0;
        Parallel.For(0, ShardCount, shard =>
        {
            Interlocked.Add(ref account, _account.AddShard(shard, child.Account));
            Interlocked.Add(ref code, _code.AddShard(shard, child.Code));
            Interlocked.Add(ref storage, _storage.AddShard(shard, child.Storage));
        });
        Report(_account, account);
        Report(_code, code);
        Report(_storage, storage);
    }

    private static void Report<TStored>(Partition<TStored> partition, long delta) where TStored : struct, IPbtNodePath<TStored>
    {
        Metrics.PbtTrieCacheMemory.AddBy(partition.Label, delta);
        Metrics.PbtTrieCacheEntries[partition.Label] = partition.EntryCount;
    }

    /// <summary>Releases retained groups without invalidating caller-owned leases.</summary>
    /// <remarks>Must not run concurrently with another write to the cache.</remarks>
    public void Clear()
    {
        Clear(_account);
        Clear(_code);
        Clear(_storage);
    }

    private static void Clear<TStored>(Partition<TStored> partition) where TStored : struct, IPbtNodePath<TStored> =>
        Report(partition, partition.Clear());

    /// <summary>Stops admission and releases all cache-owned payload references.</summary>
    public void Dispose()
    {
        _account.StopAdmission();
        _code.StopAdmission();
        _storage.StopAdmission();
        Clear();
    }

    private static long EntrySize(RefCountingMemory payload) => payload.Capacity + EntryOverhead;

    private static int ShardIndex(int hash) => (int)((uint)hash >> 24);

    /// <summary>Takes the cache's own lease on <paramref name="payload"/>.</summary>
    /// <remarks>
    /// The cache never copies a payload, whatever backs it. Memory handed to the cache must already be safe
    /// to hold for the cache's lifetime; making it so is the backing store's responsibility, not a property
    /// the consumer inspects. Do not reintroduce a copy or a backing-kind check here.
    /// </remarks>
    private static RefCountingMemory Retain(RefCountingMemory payload)
    {
        payload.AcquireLease();
        return payload;
    }

    private sealed class Partition<TStored> where TStored : struct, IPbtNodePath<TStored>
    {
        private static readonly int SlotSize = Unsafe.SizeOf<Entry<TStored>>();
        private readonly Shard<TStored>[] _shards = new Shard<TStored>[ShardCount];
        private readonly int _setCount;
        private readonly long _shardBudget;
        private bool _disposed;

        internal Partition(ulong budget, string label)
        {
            Label = label;
            long shardBudget = (long)(budget / ShardCount);
            _setCount = (int)Math.Max(1, shardBudget / (WaysPerSet * (SlotSize + EntryOverhead + AssumedPayloadSize)));
            long tableSize = 24 + _setCount * WaysPerSet * (long)SlotSize + 24 + _setCount;
            _shardBudget = Math.Max(0, shardBudget - tableSize);
            for (int index = 0; index < _shards.Length; index++) _shards[index] = new Shard<TStored>(_setCount);
        }

        internal string Label { get; }

        internal long MemorySize
        {
            get
            {
                long total = 0;
                foreach (Shard<TStored> shard in _shards) total += Volatile.Read(ref shard.MemorySize);
                return total;
            }
        }

        internal long EntryCount
        {
            get
            {
                long total = 0;
                foreach (Shard<TStored> shard in _shards) total += Volatile.Read(ref shard.EntryCount);
                return total;
            }
        }

        // The top hash byte selects the shard, so the set is drawn from the remaining bits.
        private int SetIndex(int hash) => (hash & 0xFFFFFF) % _setCount;

        internal bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            int hash = path.GetHashCode();
            return _shards[ShardIndex(hash)].TryGet(SetIndex(hash), groupHash, path, out payload);
        }

        /// <summary>Admits every group staged in the child's shard <paramref name="shardIndex"/>.</summary>
        /// <returns>The change in retained bytes.</returns>
        internal long AddShard(int shardIndex, ChildPartition<TStored> source)
        {
            if (Volatile.Read(ref _disposed)) return 0;
            Shard<TStored> shard = _shards[shardIndex];
            long delta = 0;
            foreach (ChildEntry<TStored>? entry in source.Shards[shardIndex])
            {
                if (entry is null) continue;
                long size = EntrySize(entry.Payload);
                if (size > _shardBudget) continue;
                delta += shard.Add(SetIndex(entry.Hash), entry.GroupHash, entry.Path, entry.Payload, size, _shardBudget);
            }
            return delta;
        }

        /// <returns>The change in retained bytes.</returns>
        internal long Clear()
        {
            long delta = 0;
            foreach (Shard<TStored> shard in _shards) delta += shard.Clear();
            return delta;
        }

        internal void StopAdmission() => Volatile.Write(ref _disposed, true);
    }

    /// <summary>Per-block staging cache that a <see cref="PbtTransientResource"/> carries until the block commits and <see cref="Add(PbtTransientResource)"/> folds it into the shared cache.</summary>
    /// <remarks>
    /// Partitioned and sharded like the parent so ingestion pairs shard with shard. Each shard is a direct-mapped table
    /// where a later write to the same slot supersedes the earlier one; fold workers and warmer threads write concurrently.
    /// A slot holds an immutable entry swapped by compare-and-exchange, so a reader never pairs one entry's key with
    /// another's payload, and a lookup's lease fails once an overwrite has released the payload's last reference.
    /// A RocksDB-backed payload is copied on entry so the block cache is not pinned for the life of the block.
    /// </remarks>
    public sealed class ChildCache : IDisposable
    {
        private const double UtilRatio = 0.25;
        private int _shardSize;

        public ChildCache(int capacity)
        {
            _shardSize = ShardSize(capacity);
            Account = new ChildPartition<PbtNodePath>(_shardSize);
            Code = new ChildPartition<PbtNodePath>(_shardSize);
            Storage = new ChildPartition<PbtStorageNodePath>(_shardSize);
        }

        internal ChildPartition<PbtNodePath> Account { get; }
        internal ChildPartition<PbtNodePath> Code { get; }
        internal ChildPartition<PbtStorageNodePath> Storage { get; }

        public int Count => Volatile.Read(ref Account.Count) + Volatile.Read(ref Code.Count) + Volatile.Read(ref Storage.Count);

        /// <summary>Slots per partition; the value a replacement cache is constructed with to match this one.</summary>
        public int Capacity => ShardCount * _shardSize;

        private const int PartitionCount = 3;

        private static int ShardSize(int capacity) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, (capacity + ShardCount - 1) / ShardCount));

        /// <summary>Stages a group under its path and subtree hash; a null tombstone is ignored.</summary>
        internal void Set<TPath>(in ValueHash256 groupHash, TPath path, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            if (payload is null) return;
            RefCountingMemory retained = Retain(payload);
            if (IsStorage(path)) Set(Storage, groupHash, path, retained);
            else Set(IsCode(path) ? Code : Account, groupHash, path, retained);
        }

        private static void Set<TPath, TStored>(ChildPartition<TStored> partition, in ValueHash256 groupHash, TPath path, RefCountingMemory retained)
            where TPath : struct, IPbtNodePath<TPath>
            where TStored : struct, IPbtNodePath<TStored>
        {
            int hash = path.GetHashCode();
            ref ChildEntry<TStored>? slot = ref partition.Slot(hash);
            ChildEntry<TStored>? staged = null;
            while (true)
            {
                ChildEntry<TStored>? current = Volatile.Read(ref slot);
                if (current is not null && current.Matches(hash, groupHash, path))
                {
                    ((IDisposable)retained).Dispose();
                    return;
                }
                staged ??= new ChildEntry<TStored>(hash, groupHash, path.ToPath<TStored>(), retained);
                if (Interlocked.CompareExchange(ref slot, staged, current) != current) continue;
                if (current is null) Interlocked.Increment(ref partition.Count);
                else ((IDisposable)current.Payload).Dispose();
                return;
            }
        }

        /// <summary>Leases the staged group at <paramref name="path"/> whose subtree hash is <paramref name="groupHash"/>.</summary>
        /// <returns><c>true</c> when <paramref name="payload"/> holds a caller-owned lease to release with <see cref="IDisposable.Dispose"/>.</returns>
        internal bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> =>
            IsStorage(path)
                ? TryGet(Storage, groupHash, path, out payload)
                : TryGet(IsCode(path) ? Code : Account, groupHash, path, out payload);

        private static bool TryGet<TPath, TStored>(ChildPartition<TStored> partition, in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload)
            where TPath : struct, IPbtNodePath<TPath>
            where TStored : struct, IPbtNodePath<TStored>
        {
            int hash = path.GetHashCode();
            ChildEntry<TStored>? entry = Volatile.Read(ref partition.Slot(hash));
            if (entry is null || !entry.Matches(hash, groupHash, path) || !entry.Payload.TryAcquireLease())
            {
                payload = null;
                return false;
            }
            payload = entry.Payload;
            return true;
        }

        /// <summary>Releases every staged payload, growing the tables when the block filled more than a quarter of the slots.</summary>
        /// <remarks>Only the exclusive owner may reset after all leases have drained.</remarks>
        public void Reset()
        {
            int count = Count;
            while (count > PartitionCount * Capacity * UtilRatio) _shardSize *= 2;
            Dispose();
        }

        /// <summary>Releases every staged payload without growing.</summary>
        public void Dispose()
        {
            Account.Clear(_shardSize);
            Code.Clear(_shardSize);
            Storage.Clear(_shardSize);
        }
    }

    internal sealed class ChildPartition<TStored>(int shardSize) where TStored : struct, IPbtNodePath<TStored>
    {
        internal ChildEntry<TStored>?[][] Shards = CreateShards(shardSize);
        internal int Count;
        private int _mask = shardSize - 1;

        internal ref ChildEntry<TStored>? Slot(int hash) => ref Shards[ShardIndex(hash)][hash & _mask];

        /// <summary>Releases every payload; reallocates the tables when <paramref name="shardSize"/> differs from the current one.</summary>
        internal void Clear(int shardSize)
        {
            foreach (ChildEntry<TStored>?[] shard in Shards)
                foreach (ref ChildEntry<TStored>? entry in shard.AsSpan())
                {
                    ((IDisposable?)entry?.Payload)?.Dispose();
                    entry = null;
                }
            Count = 0;
            if (shardSize == _mask + 1) return;
            Shards = CreateShards(shardSize);
            _mask = shardSize - 1;
        }

        private static ChildEntry<TStored>?[][] CreateShards(int shardSize)
        {
            ChildEntry<TStored>?[][] shards = new ChildEntry<TStored>?[ShardCount][];
            for (int index = 0; index < shards.Length; index++) shards[index] = new ChildEntry<TStored>?[shardSize];
            return shards;
        }
    }

    internal sealed class ChildEntry<TStored>(int hash, in ValueHash256 groupHash, TStored path, RefCountingMemory payload) where TStored : struct, IPbtNodePath<TStored>
    {
        internal readonly int Hash = hash;
        internal readonly ValueHash256 GroupHash = groupHash;
        internal readonly TStored Path = path;
        internal readonly RefCountingMemory Payload = payload;

        internal bool Matches<TPath>(int hash, in ValueHash256 groupHash, TPath path) where TPath : struct, IPbtNodePath<TPath> =>
            Hash == hash && GroupHash == groupHash && Path.Equals(path);
    }

    /// <remarks>Lookups are lock-free; every other member expects to be the shard's only writer.</remarks>
    private sealed class Shard<TStored>(int setCount) where TStored : struct, IPbtNodePath<TStored>
    {
        internal long MemorySize;
        internal long EntryCount;
        private readonly Entry<TStored>[] _entries = new Entry<TStored>[setCount * WaysPerSet];
        private readonly byte[] _hands = new byte[setCount];

        internal bool TryGet<TPath>(int set, in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            int first = set * WaysPerSet;
            for (int slot = first; slot < first + WaysPerSet; slot++)
            {
                ref Entry<TStored> entry = ref _entries[slot];
                int version = Volatile.Read(ref entry.Version);
                RefCountingMemory? candidate = entry.Payload;
                if ((version & 1) != 0 || candidate is null || entry.GroupHash != groupHash || !entry.Path.Equals(path)) continue;
                if (!candidate.TryAcquireLease()) continue;
                // The lease's interlocked acquire fences the field reads above, so an unchanged version proves no writer touched the slot while they were read.
                if (Volatile.Read(ref entry.Version) == version)
                {
                    entry.Referenced = true;
                    payload = candidate;
                    return true;
                }
                ((IDisposable)candidate).Dispose();
            }
            payload = null;
            return false;
        }

        /// <returns>The change in retained bytes.</returns>
        internal long Add<TPath>(int set, in ValueHash256 groupHash, TPath path, RefCountingMemory payload, long size, long budget) where TPath : struct, IPbtNodePath<TPath>
        {
            int first = set * WaysPerSet;
            int target = -1;
            int stale = -1;
            for (int slot = first; slot < first + WaysPerSet; slot++)
            {
                ref Entry<TStored> entry = ref _entries[slot];
                if (entry.Payload is null) target = target < 0 ? slot : target;
                // The path is the key, so it is compared on every occupied way: a same-path entry is either this group or a stale one to replace.
                else if (entry.Path.Equals(path))
                {
                    if (entry.GroupHash == groupHash) return 0;
                    stale = slot;
                }
            }
            long delta = 0;
            // A newer subtree hash supersedes the same path's older group, so replace it before spending a free way.
            if (stale >= 0) target = stale;
            else if (target < 0) target = ClockVictim(set, -1);
            if (_entries[target].Payload is not null) delta += Evict(target);
            while (MemorySize + size > budget)
            {
                int victim = ClockVictim(set, target);
                if (victim < 0) return delta;
                delta += Evict(victim);
            }
            ref Entry<TStored> admitted = ref _entries[target];
            Interlocked.Increment(ref admitted.Version);
            admitted.GroupHash = groupHash;
            admitted.Path = path.ToPath<TStored>();
            admitted.Payload = Retain(payload);
            admitted.Referenced = false;
            Volatile.Write(ref admitted.Version, admitted.Version + 1);
            MemorySize += size;
            EntryCount++;
            return delta + size;
        }

        /// <returns>The change in retained bytes.</returns>
        internal long Clear()
        {
            long delta = 0;
            for (int slot = 0; slot < _entries.Length; slot++)
                if (_entries[slot].Payload is not null) delta += Evict(slot);
            return delta;
        }

        /// <returns>The change in retained bytes.</returns>
        private long Evict(int slot)
        {
            ref Entry<TStored> entry = ref _entries[slot];
            RefCountingMemory payload = entry.Payload!;
            Interlocked.Increment(ref entry.Version);
            entry.Payload = null;
            Volatile.Write(ref entry.Version, entry.Version + 1);
            long size = EntrySize(payload);
            MemorySize -= size;
            EntryCount--;
            ((IDisposable)payload).Dispose();
            return -size;
        }

        /// <summary>Second-chance sweep of one set: returns the first unreferenced slot other than <paramref name="skip"/>, clearing the flags it passes, or -1 when there is none.</summary>
        private int ClockVictim(int set, int skip)
        {
            int first = set * WaysPerSet;
            int hand = _hands[set];
            for (int step = 0; step < 2 * WaysPerSet; step++)
            {
                int slot = first + hand;
                hand = (hand + 1) % WaysPerSet;
                ref Entry<TStored> entry = ref _entries[slot];
                if (entry.Payload is null || slot == skip) continue;
                if (entry.Referenced)
                {
                    entry.Referenced = false;
                    continue;
                }
                _hands[set] = (byte)hand;
                return slot;
            }
            return -1;
        }
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
