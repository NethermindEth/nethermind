// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>
/// A jemalloc-style native allocator: size-class slabs behind per-thread caches for requests up to
/// the largest class, and a dedicated exact-size native allocation above it.
/// </summary>
/// <remarks>
/// Rents and frees are safe from any thread, and a block may be freed by a thread other than the
/// one that allocated it. Small regions park in the freeing thread's cache and cross threads only
/// through a bin lock, and a caller's writes are ordered before its <see cref="Free"/> by that lock,
/// so no further fences are needed. Each gen-2 collection sweeps the thread caches and flushes those
/// idle since the previous sweep (<see cref="TrimIdleThreadCaches"/>), so a thread that stopped
/// allocating does not keep its slabs alive.
/// <para>
/// <see cref="Dispose"/> stops new allocations and releases the retained empty slabs; a slab still
/// holding live regions is released by the last <see cref="Free"/> into it, so a consumer that
/// outlives the allocator may still free its blocks.
/// </para>
/// Uses <see cref="NativeMemory.Alloc(nuint)"/> only, never an aligned allocation, so it also runs on
/// the zkVM guest.
/// </remarks>
public sealed unsafe class SlabMemoryAllocator : IDisposable
{
    /// <summary>How many page counts above the minimum a slab may grow to bring its tail waste under <see cref="SlabTailWasteDenominator"/>.</summary>
    private const int SlabPageSearchWindow = 32;
    private const int SlabTailWasteDenominator = 16;
    private const int ThreadCacheBytesPerClass = 64 * 1024;
    private const int MinimumThreadCacheCapacity = 4;

    private readonly SlabAllocatorOptions _options;
    private readonly SizeClassBin[] _bins;
    private readonly byte[] _classOfQuantum;
    private readonly int _maxSmallSize;
    private readonly ThreadLocal<SlabThreadCache> _threadCache;
    private readonly List<WeakReference<SlabThreadCache>> _threadCaches = [];
    private readonly Lock _threadCachesLock = new();
    private long _hugeBytes;
    private volatile bool _disposed;

    public SlabMemoryAllocator(SlabAllocatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        ReadOnlySpan<int> classes = options.SizeClasses;
        _maxSmallSize = classes[^1];
        _bins = new SizeClassBin[classes.Length];
        _classOfQuantum = new byte[_maxSmallSize / options.Quantum];
        int previousClass = 0;
        for (int classIndex = 0; classIndex < classes.Length; classIndex++)
        {
            int classSize = classes[classIndex];
            int cacheCapacity = Math.Clamp(ThreadCacheBytesPerClass / classSize, MinimumThreadCacheCapacity, options.ThreadCacheMaxCount);
            _bins[classIndex] = new SizeClassBin(this, classIndex, classSize, ChooseSlabSize(classSize, options.PageSize), cacheCapacity);
            for (int quantumIndex = previousClass / options.Quantum; quantumIndex < classSize / options.Quantum; quantumIndex++)
                _classOfQuantum[quantumIndex] = (byte)classIndex;
            previousClass = classSize;
        }

        _threadCache = new ThreadLocal<SlabThreadCache>(CreateThreadCache, trackAllValues: false);
        _ = new Gen2Sweeper(this);
    }

    public SlabAllocatorOptions Options => _options;
    internal bool IsDisposed => _disposed;

    /// <summary>Native bytes held: every slab plus dedicated blocks.</summary>
    public long ReservedBytes
    {
        get
        {
            long total = Volatile.Read(ref _hugeBytes);
            foreach (SizeClassBin bin in _bins) total += bin.SlabBytes;
            return total;
        }
    }

    /// <summary>Bytes handed out and not yet freed; small regions parked in thread caches count as handed out.</summary>
    public long OutstandingBytes
    {
        get
        {
            long total = Volatile.Read(ref _hugeBytes);
            foreach (SizeClassBin bin in _bins) total += bin.AllocatedBytes;
            return total;
        }
    }

    /// <summary>The capacity <see cref="Allocate"/> would give a request of <paramref name="size"/> bytes.</summary>
    public int RoundUpCapacity(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return size <= _maxSmallSize ? _bins[ClassIndex(size)].ClassSize : size;
    }

    public SlabAllocation Allocate(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size <= _maxSmallSize)
        {
            int classIndex = ClassIndex(size);
            RegionHandle region = _threadCache.Value!.Rent(classIndex);
            return new SlabAllocation(region.Slab.RegionPointer(region.Index), _bins[classIndex].ClassSize, region.Slab, region.Index);
        }

        // Safety: a dedicated block of exactly size bytes, freed by Free through the ownerless path.
        byte* huge = (byte*)NativeMemory.Alloc((nuint)size);
        GC.AddMemoryPressure(size);
        Interlocked.Add(ref _hugeBytes, size);
        return new SlabAllocation(huge, size, null, 0);
    }

    /// <summary>Gives a block back; safe after <see cref="Dispose"/> and from any thread.</summary>
    public void Free(in SlabAllocation allocation)
    {
        if (allocation.Owner is Slab slab)
        {
            RegionHandle region = new(slab, allocation.Index);
            if (_disposed) slab.Bin.FreeBatch(new ReadOnlySpan<RegionHandle>(in region));
            else _threadCache.Value!.Return(slab.Bin.ClassIndex, in region);
            return;
        }

        NativeMemory.Free(allocation.Pointer);
        GC.RemoveMemoryPressure(allocation.Capacity);
        Interlocked.Add(ref _hugeBytes, -allocation.Capacity);
    }

    /// <summary>Returns the calling thread's cached regions to the bins.</summary>
    public void FlushThreadCache()
    {
        if (_threadCache.IsValueCreated) _threadCache.Value!.Flush();
    }

    /// <summary>Returns every thread's cached regions to the bins, so <see cref="OutstandingBytes"/> counts only live blocks.</summary>
    public void FlushAllThreadCaches() => ForEachThreadCache(static cache => cache.Flush());

    /// <summary>Flushes the caches of threads that have not rented or returned since the previous call; runs after each gen-2 collection.</summary>
    public void TrimIdleThreadCaches() => ForEachThreadCache(static cache => cache.TrimIfIdle());

    public void Dispose()
    {
        if (_disposed) return;
        FlushAllThreadCaches();
        _disposed = true;
        foreach (SizeClassBin bin in _bins) bin.ReleaseSpare();
        _threadCache.Dispose();
    }

    private SlabThreadCache CreateThreadCache()
    {
        SlabThreadCache cache = new(_bins);
        lock (_threadCachesLock) _threadCaches.Add(new WeakReference<SlabThreadCache>(cache));
        return cache;
    }

    /// <remarks>Caches of dead threads have been finalized and are pruned here.</remarks>
    private void ForEachThreadCache(Action<SlabThreadCache> action)
    {
        lock (_threadCachesLock)
        {
            for (int i = _threadCaches.Count - 1; i >= 0; i--)
            {
                if (_threadCaches[i].TryGetTarget(out SlabThreadCache? cache)) action(cache);
                else _threadCaches.RemoveAt(i);
            }
        }
    }

    private int ClassIndex(int size) => size == 0 ? 0 : _classOfQuantum[(size - 1) / _options.Quantum];

    /// <summary>Runs the idle-cache sweep after each gen-2 collection by re-registering its own finalizer, until the allocator is disposed or collected.</summary>
    /// <remarks>
    /// Holds the allocator through a weak <see cref="GCHandle"/> rather than a <see cref="WeakReference{T}"/>:
    /// the latter is finalizable itself and may be torn down before this finalizer runs.
    /// </remarks>
    private sealed class Gen2Sweeper(SlabMemoryAllocator allocator)
    {
        private GCHandle _allocator = GCHandle.Alloc(allocator, GCHandleType.Weak);

        ~Gen2Sweeper()
        {
            if (_allocator.Target is SlabMemoryAllocator { _disposed: false } allocator)
            {
                allocator.TrimIdleThreadCaches();
                GC.ReRegisterForFinalize(this);
                return;
            }

            _allocator.Free();
        }
    }

    /// <summary>The smallest page multiple holding the class with at most 1/16 tail waste, or the least wasteful one in the search window.</summary>
    private static int ChooseSlabSize(int classSize, int pageSize)
    {
        int minimumPages = (classSize + pageSize - 1) / pageSize;
        int bestSize = 0;
        double bestWasteFraction = double.MaxValue;
        for (int pages = minimumPages; pages < minimumPages + SlabPageSearchWindow; pages++)
        {
            long size = (long)pages * pageSize;
            if (size > int.MaxValue) break;
            long waste = size % classSize;
            if (waste * SlabTailWasteDenominator <= size) return (int)size;
            double wasteFraction = (double)waste / size;
            if (wasteFraction < bestWasteFraction)
            {
                bestSize = (int)size;
                bestWasteFraction = wasteFraction;
            }
        }

        return bestSize;
    }
}
