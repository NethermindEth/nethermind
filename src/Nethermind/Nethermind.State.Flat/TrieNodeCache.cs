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
/// A specialized RLP cache storing trie node bytes inline in a single flat pinned array.
/// Uses a seqlock per entry so readers are lock-free while writers use a CAS-based lock.
/// On Linux, the backing memory is MADVISE'd for Transparent Huge Pages.
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

    // Approximate bytes per inline CacheEntry (long + ValueHash256 + TrieNodeRlp).
    // Used only for capacity sizing; actual memory is the pre-allocated array.
    private const int EstimatedBytesPerEntry = 592;

    // Linux THP constants
    private const int MADV_HUGEPAGE = 14;
    private const nuint HugePageSize = 2 * 1024 * 1024; // 2MB

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern unsafe int Madvise(void* addr, nuint length, int advice);

    private readonly ILogger _logger;

    /// <summary>
    /// Entry stored inline in the cache array. Pure value type — no managed references,
    /// so the array can be pinned for THP madvise.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CacheEntry
    {
        public long SeqLock;        // seqlock header
        public ValueHash256 Hash;   // node hash for collision detection (inline value type)
        public TrieNodeRlp Rlp;
    }

    private readonly CacheEntry[] _cache;
    private readonly int _totalMask;
    private readonly long _maxCacheMemoryThreshold;
    private GCHandle _gcHandle;

    public TrieNodeCache(IFlatDbConfig flatDbConfig, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<TrieNodeCache>();

        _maxCacheMemoryThreshold = flatDbConfig.TrieCacheMemoryBudget;

        int targetSize = _maxCacheMemoryThreshold > 0
            ? (int)(_maxCacheMemoryThreshold / EstimatedBytesPerEntry)
            : 0;
        int totalSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, targetSize));
        _totalMask = totalSize - 1;

        // Allocate pinned so GC won't relocate — required for madvise and THP.
        // CacheEntry is pure value type (no managed refs) so pinning is safe.
        _cache = GC.AllocateUninitializedArray<CacheEntry>(totalSize, pinned: true);
        Array.Clear(_cache); // zero-init since uninitialized

        // Pin to get the address for madvise.
        _gcHandle = GCHandle.Alloc(_cache, GCHandleType.Pinned);

        // Hint the kernel to back with huge pages on Linux.
        if (OperatingSystem.IsLinux())
        {
            nuint byteSize = (nuint)((long)totalSize * Unsafe.SizeOf<CacheEntry>());
            if (byteSize >= HugePageSize)
            {
                unsafe
                {
                    Madvise((void*)_gcHandle.AddrOfPinnedObject(), byteSize, MADV_HUGEPAGE);
                }
            }
        }

        // Report constant memory footprint (array is pre-allocated).
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = (long)totalSize * EstimatedBytesPerEntry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetIndex(Hash256? address, in TreePath path)
    {
        int h1 = address is not null ? address.GetHashCode() : 0;
        int h2 = path.GetHashCode();
        return (h1 ^ h2) & int.MaxValue;
    }

    public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, ref TrieNodeRlp rlp)
    {
        int bucketIdx = GetIndex(address, in path) & _totalMask;

        ref CacheEntry entry = ref _cache[bucketIdx];

        long h1 = Volatile.Read(ref entry.SeqLock);
        // Skip if locked (write in progress) or slot is empty.
        if (h1 < 0 || (h1 & SeqLockOccupied) == 0) return false;

        // Seqlock read: acquire barrier before reading payload.
        Thread.MemoryBarrier();
        ValueHash256 storedHash = entry.Hash;
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

        if (storedHash != hash)
        {
            rlp.Length = 0;
            return false;
        }

        return true;
    }

    public void Add(TransientResource transientResource)
    {
        if (_maxCacheMemoryThreshold == 0) return;

        ChildCache.ChildEntry[] childEntries = transientResource.Nodes.InternalEntries;
        Parallel.For(0, childEntries.Length, (j) =>
        {
            ref ChildCache.ChildEntry childEntry = ref childEntries[j];

            // Quick check: skip if locked or empty before doing the full seqlock read.
            long h1 = Volatile.Read(ref childEntry.SeqLock);
            if (h1 < 0 || (h1 & SeqLockOccupied) == 0) return;

            Thread.MemoryBarrier();
            int childHashCode = childEntry.HashCode;
            ValueHash256 nodeHash = childEntry.Hash;
            TrieNodeRlp tempRlp = childEntry.Rlp;
            Thread.MemoryBarrier();

            long h2 = Volatile.Read(ref childEntry.SeqLock);
            if (h1 != h2 || tempRlp.Length == 0) return;

            int bucketIdx = childHashCode & _totalMask;
            WriteMainEntry(bucketIdx, nodeHash, ref tempRlp);
        });

        if (_logger.IsTrace) _logger.Trace("Trie node cache updated from transient resource");
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = (long)(_totalMask + 1) * EstimatedBytesPerEntry;
    }

    private void WriteMainEntry(int bucketIdx, ValueHash256 hash, ref TrieNodeRlp rlp)
    {
        if (rlp.Length == 0 || rlp.Length > TrieNodeRlp.MaxRlpLength) return;

        ref CacheEntry entry = ref _cache[bucketIdx];
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

    /// <summary>Clears all cached RLP entries by zeroing the cache array.</summary>
    public void Clear()
    {
        Array.Clear(_cache);
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = 0;
    }

    public void Dispose()
    {
        if (_gcHandle.IsAllocated)
            _gcHandle.Free();
    }

    /// <summary>
    /// Small transient cache for use in <see cref="TransientResource"/>.
    /// Flat array like <see cref="TrieNodeCache"/> so that <see cref="Add"/> can
    /// flush entries in parallel.
    /// </summary>
    public class ChildCache
    {
        // Seqlock layout: same constants as outer class (accessible as nested class).
        // [Lock:1][...][Seq:16][Occ:1]

        /// <summary>Entry stored inline in the array.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ChildEntry
        {
            public long SeqLock;        // seqlock header
            public int HashCode;        // combined path+address hash for bucket placement
            public ValueHash256 Hash;   // node hash for collision detection (inline value type)
            public TrieNodeRlp Rlp;
        }

        private ChildEntry[] _entries;
        private int _count;
        private int _mask;

        public int Count => _count;
        public int Capacity => _entries.Length;

        /// <summary>Exposes entry array for parallel iteration in <see cref="TrieNodeCache.Add"/>.</summary>
        internal ChildEntry[] InternalEntries => _entries;

        public ChildCache(int size)
        {
            int totalSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, size));
            _entries = new ChildEntry[totalSize];
            _mask = totalSize - 1;
        }

        public void Reset()
        {
            // Grow backing array if utilization exceeded capacity since last reset.
            if (_count / 0.25 > _entries.Length)
            {
                int newTarget = (int)(_count / 0.25);
                int totalSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, newTarget));
                _entries = new ChildEntry[totalSize];
                _mask = totalSize - 1;
            }
            else
            {
                Array.Clear(_entries, 0, _entries.Length);
            }

            _count = 0;
        }

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, ref TrieNodeRlp rlp)
        {
            int hashCode = GetIndex(address, path);
            int idx = hashCode & _mask;

            ref ChildEntry entry = ref _entries[idx];

            long h1 = Volatile.Read(ref entry.SeqLock);
            if (h1 < 0 || (h1 & SeqLockOccupied) == 0) return false;

            Thread.MemoryBarrier();
            int storedHashCode = entry.HashCode;
            ValueHash256 storedHash = entry.Hash;
            rlp = entry.Rlp;
            Thread.MemoryBarrier();

            long h2 = Volatile.Read(ref entry.SeqLock);
            if (h1 != h2)
            {
                rlp.Length = 0;
                return false;
            }

            if (storedHashCode != hashCode || storedHash != hash)
            {
                rlp.Length = 0;
                return false;
            }

            return true;
        }

        public void Set(Hash256? address, in TreePath path, Hash256 hash, ReadOnlySpan<byte> rlp)
        {
            if (rlp.Length == 0 || rlp.Length > TrieNodeRlp.MaxRlpLength) return;

            int hashCode = GetIndex(address, path);
            int idx = hashCode & _mask;

            ref ChildEntry entry = ref _entries[idx];
            long existing = Volatile.Read(ref entry.SeqLock);
            if (existing < 0) return; // locked, skip

            long newSeq = ((existing & SeqLockSeqMask) + SeqLockSeqInc) & SeqLockSeqMask;
            long locked = newSeq | SeqLockLockBit;

            if (Interlocked.CompareExchange(ref entry.SeqLock, locked, existing) != existing) return;

            entry.HashCode = hashCode;
            entry.Hash = hash.ValueHash256;
            entry.Rlp.Set(rlp);

            Volatile.Write(ref entry.SeqLock, newSeq | SeqLockOccupied);

            _count++; // approximate; not atomically consistent, but only used for resize heuristics
        }
    }
}
