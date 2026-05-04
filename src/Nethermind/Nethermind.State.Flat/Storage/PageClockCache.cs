// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Threading;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Composite key identifying an OS page within an arena: (<see cref="ArenaId"/>, <see cref="PageIdx"/>).
/// <see cref="PageIdx"/> is <c>offset / Environment.SystemPageSize</c>, where <c>offset</c> is the
/// arena-absolute byte offset of the page's first byte.
/// </summary>
public readonly record struct PageKey(int ArenaId, int PageIdx);

/// <summary>
/// Page-tracking clock cache for arena-backed mmap regions. Stores no payload — only membership +
/// per-slot accessed bits. On <see cref="Touch"/>, marks the slot accessed (fast path) or installs
/// a new slot, evicting the LRU page via the clock algorithm. Eviction invokes a callback whose
/// purpose is to <c>madvise(MADV_DONTNEED)</c> the evicted OS page so the kernel can drop it.
/// Sharded by <see cref="PageKey"/> hash so each shard owns an independent clock arm + dictionary
/// + lock; this trades the previous lock-free <c>ConcurrentDictionary</c> fast path for reduced
/// contention via N independent <see cref="McsLock"/>s.
/// </summary>
public sealed class PageClockCache
{
    private const int BitShiftPerInt64 = 6;

    private readonly int _maxCapacity;
    private readonly Shard[] _shards;
    private readonly int _shardMask;
    private readonly Action<int, int>? _onEvict;
    private long _touchCount;

    public int MaxCapacity => _maxCapacity;

    public int Count
    {
        get
        {
            int sum = 0;
            foreach (Shard s in _shards) sum += Volatile.Read(ref s.Count);
            return sum;
        }
    }

    /// <summary>Total number of <see cref="Touch"/> calls observed (including fast-path hits).</summary>
    internal long TouchCount => Volatile.Read(ref _touchCount);

    public PageClockCache(int maxCapacity, Action<int, int>? onEvict = null)
        : this(maxCapacity, DefaultShardCount(maxCapacity), onEvict)
    {
    }

    internal PageClockCache(int maxCapacity, int shardCount, Action<int, int>? onEvict = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shardCount);

        _maxCapacity = maxCapacity;
        _onEvict = onEvict;

        if (maxCapacity == 0)
        {
            _shards = [new Shard(0)];
            _shardMask = 0;
            return;
        }

        // Round shardCount up to power of two, clamp so each shard gets >= 1 slot.
        int desired = (int)BitOperations.RoundUpToPowerOf2((uint)shardCount);
        if (desired > maxCapacity)
            desired = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, maxCapacity));
        if (desired > maxCapacity) desired >>= 1;
        if (desired < 1) desired = 1;

        int perShard = (maxCapacity + desired - 1) / desired;
        _shards = new Shard[desired];
        for (int i = 0; i < desired; i++) _shards[i] = new Shard(perShard);
        _shardMask = desired - 1;
    }

    private static int DefaultShardCount(int maxCapacity)
    {
        if (maxCapacity == 0) return 1;
        uint target = (uint)Math.Min(64, Math.Max(1, Environment.ProcessorCount * 4));
        return (int)BitOperations.RoundUpToPowerOf2(target);
    }

    public void Touch(int arenaId, int pageIdx)
    {
        if (_maxCapacity == 0) return;
        Interlocked.Increment(ref _touchCount);

        PageKey key = new(arenaId, pageIdx);
        Shard shard = _shards[(uint)key.GetHashCode() & (uint)_shardMask];

        PageKey evicted = default;
        bool didEvict = false;

        using (shard.Lock.Acquire())
        {
            if (shard.SlotByPage.TryGetValue(key, out int slot))
            {
                shard.MarkAccessed(slot);
                return;
            }

            int offset;
            if (shard.FreeOffsets.Count > 0)
            {
                offset = shard.FreeOffsets.Dequeue();
            }
            else if (shard.Count < shard.Capacity)
            {
                offset = shard.Count;
            }
            else
            {
                offset = shard.Replace(out evicted);
                didEvict = true;
            }

            shard.KeyToOffset[offset] = key;
            shard.SlotByPage[key] = offset;
            shard.Count++;
            // New slot starts with accessed=false — it gets a chance to survive the next clock
            // sweep. Clearing here is defensive in case the bit was left set by a prior evictee.
            shard.ClearAccessed(offset);
        }

        if (didEvict)
            _onEvict?.Invoke(evicted.ArenaId, evicted.PageIdx);
    }

    internal bool ContainsPage(int arenaId, int pageIdx)
    {
        PageKey key = new(arenaId, pageIdx);
        Shard shard = _shards[(uint)key.GetHashCode() & (uint)_shardMask];
        using (shard.Lock.Acquire())
            return shard.SlotByPage.ContainsKey(key);
    }

    public void Clear()
    {
        if (_maxCapacity == 0) return;
        foreach (Shard s in _shards)
        {
            using (s.Lock.Acquire()) s.Clear();
        }
    }

    private sealed class Shard
    {
        public readonly int Capacity;
        public readonly Dictionary<PageKey, int> SlotByPage;
        public readonly PageKey[] KeyToOffset;
        public readonly long[] HasBeenAccessedBitmap;
        public readonly Queue<int> FreeOffsets = new();
        public readonly McsLock Lock = new();
        public int Clock;
        public int Count;

        public Shard(int capacity)
        {
            Capacity = capacity;
            if (capacity == 0)
            {
                SlotByPage = new Dictionary<PageKey, int>();
                KeyToOffset = [];
                HasBeenAccessedBitmap = [];
            }
            else
            {
                SlotByPage = new Dictionary<PageKey, int>(capacity);
                KeyToOffset = new PageKey[capacity];
                HasBeenAccessedBitmap = new long[((capacity - 1) >>> BitShiftPerInt64) + 1];
            }
        }

        public void Clear()
        {
            Count = 0;
            Clock = 0;
            FreeOffsets.Clear();
            SlotByPage.Clear();
            KeyToOffset.AsSpan().Clear();
            HasBeenAccessedBitmap.AsSpan().Clear();
        }

        public int Replace(out PageKey evicted)
        {
            int position = Clock;
            int max = Count;
            Debug.Assert(max > 0);
            while (true)
            {
                if (position >= max) position = 0;

                bool accessed = ClearAccessed(position);
                if (!accessed)
                {
                    evicted = KeyToOffset[position];
                    if (!SlotByPage.Remove(evicted))
                        throw new InvalidOperationException(
                            $"{nameof(PageClockCache)} removing entry {evicted} at slot {position} that doesn't exist");

                    Count--;
                    Clock = position + 1;
                    return position;
                }

                position++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ClearAccessed(int position)
        {
            uint offset = (uint)position >> BitShiftPerInt64;
            long flags = 1L << position;
            ref long word = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(HasBeenAccessedBitmap), offset);
            bool accessed = (word & flags) != 0;
            word &= ~flags;
            return accessed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkAccessed(int position)
        {
            uint offset = (uint)position >> BitShiftPerInt64;
            long flags = 1L << position;
            ref long word = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(HasBeenAccessedBitmap), offset);
            word |= flags;
        }
    }
}
