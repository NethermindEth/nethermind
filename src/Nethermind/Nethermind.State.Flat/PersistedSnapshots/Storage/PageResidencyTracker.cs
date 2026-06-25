// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// 8-way set-associative <em>clock</em> (second-chance) page residency tracker for arena-backed
/// mmap regions. Each set occupies one 64-byte cache line (8 ways × 8 bytes); the slot value
/// packs <c>(REF | VALID | arenaId | pageIdx)</c>:
/// <list type="bullet">
///   <item>bit 63: <b>REF</b> bit — set on every touch (insert and Hit both arm it), cleared by the clock hand on a miss-pass.</item>
///   <item>bit 62: <b>VALID</b> bit — distinguishes empty (<c>0L</c>) from a present <c>(arenaId=0, pageIdx=0)</c>.</item>
///   <item>bits 32–61: <b>arenaId</b> (30 bits — ample; arena IDs are dense small ints).</item>
///   <item>bits 0–31: <b>pageIdx</b>.</item>
/// </list>
/// Hits are lock-free: scan the 8 ways with <see cref="Volatile.Read(ref long)"/>, and on a match
/// arm the REF bit via <see cref="Interlocked.Or(ref long, long)"/>. The miss path takes a 1-bit
/// per-set spinlock (stashed in a packed <c>int[]</c> meta side-array — one int per set, ~16 sets
/// per cache line, only touched on miss) and runs the clock algorithm: re-scan for a hit, then
/// for an empty way, then advance a per-set hand clearing REF bits until it finds an
/// unreferenced way to evict.
/// </summary>
/// <remarks>
/// Slot lines are 64-byte aligned via <see cref="NativeMemory.AlignedAlloc(nuint, nuint)"/>, so
/// two threads writing to different sets never invalidate each other's L1 lines on the hot path.
/// The meta side-array sees no traffic on hits, so the false-sharing it allows between concurrent
/// evictors in nearby sets is bounded to the rare miss path.
///
/// Concurrent miss-path racers may each independently elect different victims and report
/// different evicted pages; redundant <c>madvise(MADV_DONTNEED)</c> on the same page is wasted
/// work but harmless.
/// </remarks>
public sealed unsafe class PageResidencyTracker : IDisposable
{
    /// <summary>
    /// Outcome of a <see cref="TryTouch"/> call. Lets the caller distinguish "page is already
    /// cached residency-wise" (do nothing) from "page is newly tracked" (e.g. pre-fault it) and
    /// "page displaced an unrelated occupant" (drop the displaced page).
    /// </summary>
    public enum TouchOutcome
    {
        /// <summary>The set already held this exact <c>(arenaId, pageIdx)</c>.</summary>
        Hit,
        /// <summary>The set had an empty way and now holds <c>(arenaId, pageIdx)</c>.</summary>
        Inserted,
        /// <summary>The set was full of unreferenced pages; the clock victim was displaced and the out parameters carry its key.</summary>
        Evicted,
    }

    private const long RefBit = unchecked((long)0x8000_0000_0000_0000UL);
    private const long ValidBit = 0x4000_0000_0000_0000L;
    // Mask used to compare a slot against a packed key — strips REF, keeps VALID + arenaId + pageIdx.
    private const long KeyMask = ~RefBit;
    private const long ArenaIdMask = 0x3FFF_FFFFL; // 30 bits
    private const int Ways = 8;
    private const int WayShift = 3; // log2(Ways)
    private const int WayMask = Ways - 1;
    private const int CacheLineBytes = 64;
    private const int MetaLockBit = 1 << 7;
    private const int MetaHandMask = 0x7;
    // Cap on slots the keep-warm hand will probe in a single TryPickResidentPage call before
    // giving up — bounds the cost when the tracker is mostly empty.
    private const int MaxWarmProbe = 16;

    // _slots: _setCount sets, each Ways longs (one cache line). 64-byte aligned.
    private long* _slots;
    // _meta: one int per set, packed (no per-set padding). bit 7 = lock; bits 0..2 = clock hand.
    private int* _meta;
    private int _disposed;
    private readonly int _setCount;
    private readonly int _setMask;
    private readonly long _metadataBytes;
    private readonly long _pageBytes;
    private long _residentPages;
    // High-water mark of resident pages whose footprint has been reported to the GC via
    // AddMemoryPressure. Monotonically non-decreasing during the tracker's lifetime,
    // bounded by MaxCapacity. Forget never shrinks it; Dispose releases it in one call.
    private long _reportedPages;
    // Monotonically-incrementing slot index advanced by TryPickResidentPage. Modded by total
    // slot count to wrap; producers race cleanly via Interlocked.Increment.
    private long _warmHand;

    public int MaxCapacity => _setCount * Ways;

    /// <summary>Bytes of unmanaged tracker metadata reported to the GC.</summary>
    public long MetadataBytes => _metadataBytes;

    /// <summary>Estimated kernel-resident bytes currently bounded by this tracker (Inserted pages × OS page size).</summary>
    public long ResidentBytes => Volatile.Read(ref _residentPages) * _pageBytes;

    internal int Count
    {
        get
        {
            int count = 0;
            long* p = _slots;
            long* end = _slots + ((nint)_setCount << WayShift);
            for (; p < end; p++)
                if ((Volatile.Read(ref *p) & ValidBit) != 0) count++;
            return count;
        }
    }

    /// <summary>
    /// Construct a tracker sized from a byte budget — divides by the OS page size to derive the
    /// slot count, then rounds up to a power-of-two number of 8-way sets. Non-positive budgets
    /// yield a 0-capacity (disabled) tracker.
    /// </summary>
    public static PageResidencyTracker FromByteBudget(long bytes)
    {
        if (bytes <= 0) return new PageResidencyTracker(0);
        int capacity = (int)Math.Min(int.MaxValue, bytes / Environment.SystemPageSize);
        return new PageResidencyTracker(capacity);
    }

    public PageResidencyTracker(int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);

        if (maxCapacity == 0)
        {
            _slots = null;
            _meta = null;
            _setCount = 0;
            _setMask = 0;
            _metadataBytes = 0;
            _pageBytes = 0;
            return;
        }

        int requestedSets = Math.Max(1, (maxCapacity + Ways - 1) >> WayShift);
        _setCount = (int)BitOperations.RoundUpToPowerOf2((uint)requestedSets);
        _setMask = _setCount - 1;

        nuint slotBytes = (nuint)_setCount * CacheLineBytes;
        _slots = (long*)NativeMemory.AlignedAlloc(slotBytes, CacheLineBytes);
        NativeMemory.Clear(_slots, slotBytes);

        nuint metaBytes = (nuint)_setCount * sizeof(int);
        _meta = (int*)NativeMemory.AlignedAlloc(metaBytes, CacheLineBytes);
        NativeMemory.Clear(_meta, metaBytes);

        _metadataBytes = (long)(slotBytes + metaBytes);
        _pageBytes = Environment.SystemPageSize;
        GC.AddMemoryPressure(_metadataBytes);
    }

    /// <summary>
    /// Records <paramref name="arenaId"/>/<paramref name="pageIdx"/> as recently touched and
    /// returns the outcome: <see cref="TouchOutcome.Hit"/> when the set already held this exact
    /// key (REF bit re-armed), <see cref="TouchOutcome.Inserted"/> when an empty way absorbed it,
    /// or <see cref="TouchOutcome.Evicted"/> when the clock hand displaced an unreferenced
    /// occupant (out parameters carry the displaced key). Disabled trackers
    /// (<see cref="MaxCapacity"/> == 0) always return <see cref="TouchOutcome.Hit"/>.
    /// </summary>
    public TouchOutcome TryTouch(int arenaId, int pageIdx, out int evictedArenaId, out int evictedPageIdx)
    {
        evictedArenaId = 0;
        evictedPageIdx = 0;

        if (_setCount == 0) return TouchOutcome.Hit;

        long key = PackKey(arenaId, pageIdx);
        int setIdx = (int)(Mix(key) & (uint)_setMask);
        long* setBase = _slots + ((nint)setIdx << WayShift);

        // Hot path: lock-free scan. Arm REF only when not already set to avoid a spurious atomic on the common re-touch case.
        for (int w = 0; w < Ways; w++)
        {
            long s = Volatile.Read(ref setBase[w]);
            if ((s & KeyMask) == key)
            {
                if ((s & RefBit) == 0)
                    Interlocked.Or(ref setBase[w], RefBit);
                return TouchOutcome.Hit;
            }
        }

        return MissPath(setIdx, setBase, key, out evictedArenaId, out evictedPageIdx);
    }

    private TouchOutcome MissPath(int setIdx, long* setBase, long key, out int evictedArenaId, out int evictedPageIdx)
    {
        evictedArenaId = 0;
        evictedPageIdx = 0;

        ref int meta = ref Unsafe.AsRef<int>(_meta + setIdx);
        AcquireSetLock(ref meta);

        try
        {
            // Re-scan under the lock — another thread may have inserted this same key while we
            // were spinning, in which case we must not double-insert it.
            for (int w = 0; w < Ways; w++)
            {
                long s = setBase[w];
                if ((s & KeyMask) == key)
                {
                    Volatile.Write(ref setBase[w], s | RefBit);
                    return TouchOutcome.Hit;
                }
            }

            // Look for an empty way (VALID=0). New arrivals arm REF=1 so they survive the
            // first clock pass.
            for (int w = 0; w < Ways; w++)
            {
                if (setBase[w] == 0L)
                {
                    Volatile.Write(ref setBase[w], key | RefBit);
                    long resident = Interlocked.Increment(ref _residentPages);
                    Debug.Assert(resident <= MaxCapacity, "_residentPages exceeds MaxCapacity");
                    // Ratchet the GC-reported high-water mark up to current occupancy. The CAS
                    // bumps _reportedPages directly to `resident` and reports the delta. Racing
                    // Inserts either short-circuit (high-water already past `resident`) or retry
                    // once with the residual delta — total reported pressure tracks the peak
                    // _residentPages reached, bounded by MaxCapacity * _pageBytes.
                    long reported;
                    while ((reported = Volatile.Read(ref _reportedPages)) < resident)
                    {
                        if (Interlocked.CompareExchange(ref _reportedPages, resident, reported) == reported)
                        {
                            GC.AddMemoryPressure((resident - reported) * _pageBytes);
                            break;
                        }
                    }
                    return TouchOutcome.Inserted;
                }
            }

            // Set is full — run the clock. Worst case: 8 set-REFs ⇒ one full pass clears them,
            // second pass finds an unreferenced way. Bound the loop at 2*Ways iterations.
            int hand = meta & MetaHandMask;
            for (int i = 0; i < 2 * Ways; i++)
            {
                long s = setBase[hand];
                if ((s & RefBit) != 0)
                {
                    Volatile.Write(ref setBase[hand], s & ~RefBit);
                    hand = (hand + 1) & WayMask;
                    continue;
                }

                evictedArenaId = (int)((s >> 32) & ArenaIdMask);
                evictedPageIdx = (int)s;
                Volatile.Write(ref setBase[hand], key | RefBit);
                hand = (hand + 1) & WayMask;
                meta = (meta & ~MetaHandMask) | hand;
                return TouchOutcome.Evicted;
            }

            // Unreachable: 2*Ways passes guarantees a victim. Fall through defensively.
            Debug.Fail("Clock scan failed to find a victim");
            return TouchOutcome.Hit;
        }
        finally
        {
            ReleaseSetLock(ref meta);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireSetLock(ref int meta)
    {
        SpinWait spinner = default;
        while (true)
        {
            int observed = Volatile.Read(ref meta);
            if ((observed & MetaLockBit) == 0)
            {
                int withLock = observed | MetaLockBit;
                if (Interlocked.CompareExchange(ref meta, withLock, observed) == observed)
                    return;
            }
            spinner.SpinOnce();
        }
    }

    // Lock holder writes meta directly; release with Volatile.Write so prior slot writes
    // publish before the lock bit clears.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseSetLock(ref int meta) =>
        Volatile.Write(ref meta, meta & ~MetaLockBit);

    /// <summary>
    /// Atomically remove <c>(arenaId, pageIdx)</c> from the tracker if present. Used by the
    /// whole-range <c>madvise(MADV_DONTNEED)</c> paths so that a snapshot's pages aren't left
    /// "tracked" after the kernel drops them — keeps the tracker in sync with actual page
    /// residency. Lock-free CAS-with-retry; a concurrent hot-path REF arm or a miss-path
    /// replacement races cleanly (we either clear the matching slot or observe the new
    /// occupant and stop).
    /// </summary>
    /// <returns><c>true</c> if a tracked entry was removed; <c>false</c> if the page was not tracked.</returns>
    public bool Forget(int arenaId, int pageIdx)
    {
        if (_setCount == 0) return false;
        long key = PackKey(arenaId, pageIdx);
        int setIdx = (int)(Mix(key) & (uint)_setMask);
        long* setBase = _slots + ((nint)setIdx << WayShift);
        for (int w = 0; w < Ways; w++)
        {
            SpinWait spinner = default;
            while (true)
            {
                long observed = Volatile.Read(ref setBase[w]);
                // Not (or no longer) our key — either never matched, or a miss-path evictor
                // overwrote it; either way the slot is no longer ours to clear.
                if ((observed & KeyMask) != key) break;
                if (Interlocked.CompareExchange(ref setBase[w], 0L, observed) == observed)
                {
                    // Slot cleared — decrement the resident-pages gauge so it tracks actual
                    // occupancy. GC pressure is a high-water mark of peak occupancy, not the
                    // current value: Forget never shrinks it, so a Forget+Insert cycle on the
                    // same slot won't add more pressure (the high-water already covers it).
                    Interlocked.Decrement(ref _residentPages);
                    return true;
                }
                // Lost the race against a REF flip — re-read and retry; CAS will succeed once
                // we observe the new (key | newRef) state.
                spinner.SpinOnce();
            }
        }
        return false;
    }

    public bool ContainsPage(int arenaId, int pageIdx)
    {
        if (_setCount == 0) return false;
        long key = PackKey(arenaId, pageIdx);
        int setIdx = (int)(Mix(key) & (uint)_setMask);
        long* setBase = _slots + ((nint)setIdx << WayShift);
        for (int w = 0; w < Ways; w++)
            if ((Volatile.Read(ref setBase[w]) & KeyMask) == key) return true;
        return false;
    }

    /// <summary>
    /// Advance the keep-warm hand and surface the next slot whose <c>VALID</c> bit is set,
    /// returning its <c>(arenaId, pageIdx)</c>. Every VALID slot is, by definition, a page the
    /// tracker is bookkeeping as resident — i.e. a page we don't want the kernel to drop — so any
    /// hit is a fine warming target. Returns <c>false</c> when the probe budget
    /// (<see cref="MaxWarmProbe"/>) runs out without finding a resident slot or when the tracker
    /// is disabled.
    /// </summary>
    /// <remarks>
    /// Lock-free: a single <see cref="Interlocked.Increment(ref long)"/> on the global hand plus
    /// one <see cref="Volatile.Read(ref long)"/> per probed slot. Concurrent callers receive
    /// disjoint slot indices on each call. Racing with a miss-path replacement may surface a key
    /// whose arena has just been disposed; the caller's dict + lease checks handle that cleanly.
    /// </remarks>
    public bool TryPickResidentPage(out int arenaId, out int pageIdx)
    {
        arenaId = 0;
        pageIdx = 0;
        if (_setCount == 0) return false;

        int totalSlots = _setCount << WayShift;
        int mask = totalSlots - 1; // _setCount is power-of-two ⇒ totalSlots is power-of-two
        for (int probe = 0; probe < MaxWarmProbe; probe++)
        {
            long hand = Interlocked.Increment(ref _warmHand);
            long slot = Volatile.Read(ref _slots[(int)((ulong)hand & (uint)mask)]);
            if ((slot & ValidBit) == 0) continue;
            arenaId = (int)((slot >> 32) & ArenaIdMask);
            pageIdx = (int)slot;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_slots is not null)
        {
            NativeMemory.AlignedFree(_slots);
            _slots = null;
        }
        if (_meta is not null)
        {
            NativeMemory.AlignedFree(_meta);
            _meta = null;
        }
        long reported = Interlocked.Exchange(ref _reportedPages, 0);
        Interlocked.Exchange(ref _residentPages, 0);
        long pressure = _metadataBytes + reported * _pageBytes;
        if (pressure > 0)
            GC.RemoveMemoryPressure(pressure);
        GC.SuppressFinalize(this);
    }

    ~PageResidencyTracker() => Dispose();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackKey(int arenaId, int pageIdx)
    {
        Debug.Assert(((uint)arenaId & ~(uint)ArenaIdMask) == 0, "arenaId exceeds 30-bit range");
        return ValidBit | (((long)arenaId & ArenaIdMask) << 32) | (uint)pageIdx;
    }

    // Multiplicative (Fibonacci) mix; uses the high bits, which give a better
    // set distribution than the low bits of (arenaId, pageIdx) when arenaId is
    // in {0..few} and pageIdx is a dense counter.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mix(long packed) =>
        (uint)(((ulong)packed * 0x9E3779B97F4A7C15UL) >> 32);
}
