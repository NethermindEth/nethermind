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
/// A shard's slots are one inline table allocated up front, sized from the partition budget; writers serialize on the shard lock.
/// Lookups take no lock: each slot carries a seqlock version that is odd while a writer changes it, and a hit
/// leases the payload first and then re-reads the version, discarding the lease if the slot moved underneath it.
/// Account and code entries key on the narrower <see cref="PbtNodePath"/>; only the storage partition pays for <see cref="PbtStorageNodePath"/>.
/// </remarks>
public sealed class PbtTrieNodeCache(IPbtConfig config) : IDisposable
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

    /// <summary>Retains a group under its path and subtree hash, superseding an older hash at the same path; the caller keeps its own lease.</summary>
    /// <remarks>A payload with more than 20% pool slack, or one borrowed from RocksDB, is copied rather than leased.</remarks>
    public void Add<TPath>(in ValueHash256 groupHash, TPath path, RefCountingMemory payload) where TPath : struct, IPbtNodePath<TPath>
    {
        if (IsStorage(path)) Add(_storage, groupHash, path, payload);
        else Add(IsCode(path) ? _code : _account, groupHash, path, payload);
    }

    private static void Add<TPath, TStored>(Partition<TStored> partition, in ValueHash256 groupHash, TPath path, RefCountingMemory payload)
        where TPath : struct, IPbtNodePath<TPath>
        where TStored : struct, IPbtNodePath<TStored>
    {
        Metrics.PbtTrieCacheMemory.AddBy(partition.Label, partition.Add(groupHash, path, payload));
        Metrics.PbtTrieCacheEntries[partition.Label] = partition.EntryCount;
    }

    /// <summary>Releases retained groups without invalidating caller-owned leases.</summary>
    public void Clear()
    {
        Clear(_account);
        Clear(_code);
        Clear(_storage);
    }

    private static void Clear<TStored>(Partition<TStored> partition) where TStored : struct, IPbtNodePath<TStored>
    {
        Metrics.PbtTrieCacheMemory.AddBy(partition.Label, partition.Clear());
        Metrics.PbtTrieCacheEntries[partition.Label] = partition.EntryCount;
    }

    /// <summary>Stops admission and releases all cache-owned payload references.</summary>
    public void Dispose()
    {
        _account.StopAdmission();
        _code.StopAdmission();
        _storage.StopAdmission();
        Clear();
    }

    // RocksDB memory pins a block-cache block, so it is always copied; a pooled buffer is copied only when its slack exceeds a fifth of the value.
    private static bool ShouldCopy(RefCountingMemory payload) => payload.IsRocksDbBacked || payload.Capacity * 5L > payload.GetSpan().Length * 6L;

    private static long EntrySize(RefCountingMemory payload) => (ShouldCopy(payload) ? payload.GetSpan().Length : payload.Capacity) + EntryOverhead;

    private static RefCountingMemory Retain(RefCountingMemory payload)
    {
        if (ShouldCopy(payload)) return RefCountingMemory.Wrapping(payload.GetSpan().ToArray());
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

        private static int ShardIndex(int hash) => (int)((uint)hash >> 24);

        // The top hash byte selects the shard, so the set is drawn from the remaining bits.
        private int SetIndex(int hash) => (hash & 0xFFFFFF) % _setCount;

        internal bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            int hash = path.GetHashCode();
            return _shards[ShardIndex(hash)].TryGet(SetIndex(hash), groupHash, path, out payload);
        }

        /// <returns>The change in retained bytes.</returns>
        internal long Add<TPath>(in ValueHash256 groupHash, TPath path, RefCountingMemory payload) where TPath : struct, IPbtNodePath<TPath>
        {
            long size = EntrySize(payload);
            if (size > _shardBudget) return 0;
            int hash = path.GetHashCode();
            Shard<TStored> shard = _shards[ShardIndex(hash)];
            lock (shard.Sync)
                return Volatile.Read(ref _disposed) ? 0 : shard.Add(SetIndex(hash), groupHash, path, payload, size, _shardBudget);
        }

        /// <returns>The change in retained bytes.</returns>
        internal long Clear()
        {
            long delta = 0;
            foreach (Shard<TStored> shard in _shards)
                lock (shard.Sync) delta += shard.Clear();
            return delta;
        }

        internal void StopAdmission() => Volatile.Write(ref _disposed, true);
    }

    /// <remarks>Lookups are lock-free; every other member expects the caller to hold <see cref="Sync"/>.</remarks>
    private sealed class Shard<TStored>(int setCount) where TStored : struct, IPbtNodePath<TStored>
    {
        internal readonly Lock Sync = new();
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
