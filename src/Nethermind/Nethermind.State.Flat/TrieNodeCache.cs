// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A specialized RLP cache storing trie node bytes inline in fixed-size value-type entries.
/// Uses a seqlock per entry so readers are lock-free while writers use a CAS-based lock.
/// The 256-shard design groups entries by tree path, enabling coherent shard eviction.
/// </summary>
public sealed class TrieNodeCache : ITrieNodeCache
{
    // Seqlock layout: [Lock:1][...padding...][Seq:16][Occ:1]
    // Lock (bit 63): set during writes; readers that see this treat the entry as a miss.
    // Seq  (bits 1-16): sequence counter incremented on every successful write; detects torn reads.
    // Occ  (bit 0): set when the slot contains valid data.
    private const long SeqLockLockBit = unchecked((long)0x8000_0000_0000_0000);
    private const long SeqLockOccupied = 1L;
    private const long SeqLockSeqMask = 0x0000_0000_0001_FFFE;
    private const long SeqLockSeqInc = 0x0000_0000_0000_0002;

    // Approximate bytes per inline CacheEntry (long + Hash256 ref + TrieNodeRlp).
    // Used only for bucket-count sizing; actual memory is the pre-allocated array.
    private const int EstimatedBytesPerEntry = 572;

    private const int ShardCount = 256;

    private readonly ILogger _logger;

    /// <summary>
    /// Entry stored inline in the shard array. No heap allocation per cached node.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CacheEntry
    {
        public long SeqLock;   // seqlock header
        public Hash256? Hash;  // node hash for collision detection (reference, already live on heap)
        public TrieNodeRlp Rlp;
    }

    private readonly CacheEntry[][] _cacheShards;
    private readonly int _bucketSize;
    private readonly int _bucketMask;
    private readonly long _maxCacheMemoryThreshold;

    public TrieNodeCache(IFlatDbConfig flatDbConfig, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<TrieNodeCache>();

        _maxCacheMemoryThreshold = flatDbConfig.TrieCacheMemoryBudget;

        // Size the arrays so that pre-allocated memory ≈ TrieCacheMemoryBudget.
        int targetBucketSize = _maxCacheMemoryThreshold > 0
            ? (int)(_maxCacheMemoryThreshold / EstimatedBytesPerEntry / ShardCount)
            : 0;
        _bucketSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, targetBucketSize));
        _bucketMask = _bucketSize - 1;

        _cacheShards = new CacheEntry[ShardCount][];
        for (int i = 0; i < ShardCount; i++)
        {
            _cacheShards[i] = new CacheEntry[_bucketSize];
        }

        // Report constant memory footprint (arrays are pre-allocated).
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = (long)_bucketSize * ShardCount * EstimatedBytesPerEntry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int shardIdx, int hashCode) GetShardAndHashCode(Hash256? address, in TreePath path)
    {
        int shardIdx = path.Path.Bytes[0];
        int h1;

        if (address is not null)
        {
            shardIdx = (shardIdx + address.Bytes[0]) % 256;
            h1 = address.GetHashCode();
        }
        else
        {
            h1 = 0;
        }

        int h2 = path.GetHashCode();
        int hashCode = (h1 ^ h2) & int.MaxValue;

        return (shardIdx, hashCode);
    }

    public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, ref TrieNodeRlp rlp)
    {
        (int shardIdx, int hashCode) = GetShardAndHashCode(address, in path);
        int bucketIdx = hashCode & _bucketMask;

        ref CacheEntry entry = ref _cacheShards[shardIdx][bucketIdx];

        long h1 = Volatile.Read(ref entry.SeqLock);
        // Skip if locked (write in progress) or slot is empty.
        if (h1 < 0 || (h1 & SeqLockOccupied) == 0) return false;

        // Seqlock read: acquire barrier before reading payload.
        Thread.MemoryBarrier();
        Hash256? storedHash = entry.Hash;
        rlp = entry.Rlp;
        // Release barrier before re-reading the header.
        Thread.MemoryBarrier();

        long h2 = Volatile.Read(ref entry.SeqLock);
        if (h1 != h2)
        {
            // Torn read — a write occurred while we were reading; treat as miss.
            rlp.Length = 0;
            return false;
        }

        if (storedHash is null || storedHash != hash)
        {
            rlp.Length = 0;
            return false;
        }

        return true;
    }

    public void Add(TransientResource transientResource)
    {
        if (_maxCacheMemoryThreshold == 0) return;

        Parallel.For(0, ShardCount, (i) =>
        {
            ChildCache.ChildEntry[] shard = transientResource.Nodes.InternalShards[i];
            for (int j = 0; j < shard.Length; j++)
            {
                ref ChildCache.ChildEntry childEntry = ref shard[j];

                // Quick check: skip if locked or empty before doing the full seqlock read.
                long h1 = Volatile.Read(ref childEntry.SeqLock);
                if (h1 < 0 || (h1 & SeqLockOccupied) == 0) continue;

                Thread.MemoryBarrier();
                int childHashCode = childEntry.HashCode;
                Hash256? nodeHash = childEntry.Hash;
                TrieNodeRlp tempRlp = childEntry.Rlp;
                Thread.MemoryBarrier();

                long h2 = Volatile.Read(ref childEntry.SeqLock);
                if (h1 != h2 || nodeHash is null || tempRlp.Length == 0) continue;

                int bucketIdx = childHashCode & _bucketMask;
                WriteMainEntry(i, bucketIdx, nodeHash, ref tempRlp);
            }
        });

        if (_logger.IsTrace) _logger.Trace("Trie node cache updated from transient resource");
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = (long)_bucketSize * ShardCount * EstimatedBytesPerEntry;
    }

    private void WriteMainEntry(int shardIdx, int bucketIdx, Hash256 hash, ref TrieNodeRlp rlp)
    {
        if (rlp.Length == 0 || rlp.Length > TrieNodeRlp.MaxRlpLength) return;

        ref CacheEntry entry = ref _cacheShards[shardIdx][bucketIdx];
        long existing = Volatile.Read(ref entry.SeqLock);
        if (existing < 0) return; // locked by another writer, skip

        long newSeq = ((existing & SeqLockSeqMask) + SeqLockSeqInc) & SeqLockSeqMask;
        long locked = newSeq | SeqLockLockBit;

        // CAS to acquire the write lock. Skip on contention rather than spin.
        if (Interlocked.CompareExchange(ref entry.SeqLock, locked, existing) != existing) return;

        entry.Hash = hash;
        entry.Rlp = rlp; // inline copy of 546 bytes

        // Release: increment seq and set occupied; clear lock bit.
        Volatile.Write(ref entry.SeqLock, newSeq | SeqLockOccupied);
    }

    /// <summary>Clears all cached RLP entries by zeroing all shard arrays.</summary>
    public void Clear()
    {
        for (int i = 0; i < ShardCount; i++)
        {
            Array.Clear(_cacheShards[i]);
        }

        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = 0;
    }

    /// <summary>
    /// Small transient cache for use in <see cref="TransientResource"/>.
    /// Sharded the same way as <see cref="TrieNodeCache"/> so that <see cref="Add"/> can
    /// flush each shard in parallel.
    /// </summary>
    public class ChildCache
    {
        // Seqlock layout: same constants as outer class (accessible as nested class).
        // [Lock:1][...][Seq:16][Occ:1]

        /// <summary>Entry stored inline in the shard array.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ChildEntry
        {
            public long SeqLock;     // seqlock header
            public int HashCode;     // combined path+address hash for bucket placement
            public Hash256? Hash;    // node keccak for collision detection
            public TrieNodeRlp Rlp;
        }

        private readonly ChildEntry[][] _shards;
        private int _count = 0;
        private int _mask;
        private int _shardSize;

        public int Count => _count;
        public int Capacity => _shards.Length * _shardSize;

        /// <summary>Exposes shard arrays for parallel iteration in <see cref="TrieNodeCache.Add"/>.</summary>
        internal ChildEntry[][] InternalShards => _shards;

        public ChildCache(int size)
        {
            int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(size + ShardCount - 1) / ShardCount);
            _shards = new ChildEntry[ShardCount][];
            _mask = powerOfTwoSize - 1;
            _shardSize = powerOfTwoSize;
            AllocateShards(_shardSize);
        }

        private void AllocateShards(int size)
        {
            for (int i = 0; i < ShardCount; i++) _shards[i] = new ChildEntry[size];
        }

        public void Reset()
        {
            // Grow backing arrays if utilization exceeded capacity since last reset.
            if (_count / 0.25 > ShardCount * _shardSize)
            {
                int newTarget = (int)(_count / 0.25);
                int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(newTarget + ShardCount - 1) / ShardCount);
                _shardSize = powerOfTwoSize;
                _mask = powerOfTwoSize - 1;
                AllocateShards(_shardSize);
            }
            else
            {
                for (int i = 0; i < ShardCount; i++)
                {
                    Array.Clear(_shards[i], 0, _shards[i].Length);
                }
            }

            _count = 0;
        }

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, ref TrieNodeRlp rlp)
        {
            (int shardIdx, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            ref ChildEntry entry = ref _shards[shardIdx][idx];

            long h1 = Volatile.Read(ref entry.SeqLock);
            if (h1 < 0 || (h1 & SeqLockOccupied) == 0) return false;

            Thread.MemoryBarrier();
            int storedHashCode = entry.HashCode;
            Hash256? storedHash = entry.Hash;
            rlp = entry.Rlp;
            Thread.MemoryBarrier();

            long h2 = Volatile.Read(ref entry.SeqLock);
            if (h1 != h2)
            {
                rlp.Length = 0;
                return false;
            }

            if (storedHashCode != hashCode || storedHash is null || storedHash != hash)
            {
                rlp.Length = 0;
                return false;
            }

            return true;
        }

        public void Set(Hash256? address, in TreePath path, Hash256 hash, ReadOnlySpan<byte> rlp)
        {
            if (rlp.Length == 0 || rlp.Length > TrieNodeRlp.MaxRlpLength) return;

            (int shardIdx, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            ref ChildEntry entry = ref _shards[shardIdx][idx];
            long existing = Volatile.Read(ref entry.SeqLock);
            if (existing < 0) return; // locked, skip

            long newSeq = ((existing & SeqLockSeqMask) + SeqLockSeqInc) & SeqLockSeqMask;
            long locked = newSeq | SeqLockLockBit;

            if (Interlocked.CompareExchange(ref entry.SeqLock, locked, existing) != existing) return;

            entry.HashCode = hashCode;
            entry.Hash = hash;
            entry.Rlp.Set(rlp);

            Volatile.Write(ref entry.SeqLock, newSeq | SeqLockOccupied);

            _count++; // approximate; not atomically consistent, but only used for resize heuristics
        }
    }
}
