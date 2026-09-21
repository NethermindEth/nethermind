// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>
/// A jemalloc-style native allocator: size-class slabs behind per-thread caches for small requests,
/// a buddy allocator over pages of native chunks for large ones, and dedicated allocations above
/// the chunk size.
/// </summary>
/// <remarks>
/// Rents and frees are safe from any thread, and a block may be freed by a thread other than the
/// one that allocated it. Small regions park in the freeing thread's cache and cross threads only
/// through a bin lock, and a caller's writes are ordered before its <see cref="Free"/> by that lock,
/// so no further fences are needed. Lock order is bin then page, never the reverse.
/// <para>
/// <see cref="Dispose"/> stops new allocations and releases chunks with nothing outstanding; a chunk
/// still holding live blocks is released by the last <see cref="Free"/> on it, so a consumer that
/// outlives the allocator may still free its blocks.
/// </para>
/// Uses <see cref="NativeMemory.Alloc(nuint)"/> only, never an aligned allocation, so it also runs on
/// the zkVM guest.
/// </remarks>
public sealed unsafe class SlabMemoryAllocator : IDisposable
{
    /// <summary>Largest page run a slab spans by preference; a class only goes above it when nothing smaller holds one region.</summary>
    private const int PreferredMaxSlabOrder = 5;
    private const int SlabTailWasteDenominator = 16;
    private const int ThreadCacheBytesPerClass = 64 * 1024;
    private const int MinimumThreadCacheCapacity = 4;

    private readonly SlabAllocatorOptions _options;
    private readonly SizeClassBin[] _bins;
    private readonly byte[] _classOfQuantum;
    private readonly int _maxSmallSize;
    private readonly ThreadLocal<SlabThreadCache> _threadCache;
    private readonly Lock _pageLock = new();
    private readonly List<SlabChunk> _chunks = [];
    private long _largeBytes;
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
        int chunkMaxOrder = BitOperations.Log2((uint)(options.ChunkSize / options.PageSize));
        int previousClass = 0;
        for (int classIndex = 0; classIndex < classes.Length; classIndex++)
        {
            int classSize = classes[classIndex];
            int order = ChooseSlabOrder(classSize, options.PageSize, chunkMaxOrder);
            int regionCount = (options.PageSize << order) / classSize;
            int cacheCapacity = Math.Clamp(ThreadCacheBytesPerClass / classSize, MinimumThreadCacheCapacity, options.ThreadCacheMaxCount);
            _bins[classIndex] = new SizeClassBin(this, classIndex, classSize, order, regionCount, cacheCapacity);
            for (int quantumIndex = previousClass / options.Quantum; quantumIndex < classSize / options.Quantum; quantumIndex++)
                _classOfQuantum[quantumIndex] = (byte)classIndex;
            previousClass = classSize;
        }

        _threadCache = new ThreadLocal<SlabThreadCache>(() => new SlabThreadCache(_bins), trackAllValues: false);
    }

    public SlabAllocatorOptions Options => _options;
    internal bool IsDisposed => _disposed;

    /// <summary>Native bytes held: every chunk plus dedicated huge blocks.</summary>
    public long ReservedBytes
    {
        get
        {
            lock (_pageLock) return (long)_chunks.Count * _options.ChunkSize + Volatile.Read(ref _hugeBytes);
        }
    }

    /// <summary>Bytes handed out and not yet freed; small regions parked in thread caches count as handed out.</summary>
    public long OutstandingBytes
    {
        get
        {
            long total = Volatile.Read(ref _largeBytes) + Volatile.Read(ref _hugeBytes);
            foreach (SizeClassBin bin in _bins) total += bin.AllocatedBytes;
            return total;
        }
    }

    public int ChunkCount
    {
        get
        {
            lock (_pageLock) return _chunks.Count;
        }
    }

    /// <summary>The capacity <see cref="Allocate"/> would give a request of <paramref name="size"/> bytes.</summary>
    public int RoundUpCapacity(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size <= _maxSmallSize) return _bins[ClassIndex(size)].ClassSize;
        return size <= _options.ChunkSize ? _options.PageSize << LargeOrder(size) : size;
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

        if (size <= _options.ChunkSize) return AllocateLarge(LargeOrder(size));

        // Safety: a dedicated block of exactly size bytes, freed by Free through the huge path.
        byte* huge = (byte*)NativeMemory.Alloc((nuint)size);
        GC.AddMemoryPressure(size);
        Interlocked.Add(ref _hugeBytes, size);
        return new SlabAllocation(huge, size, null, 0);
    }

    /// <summary>Gives a block back; safe after <see cref="Dispose"/> and from any thread.</summary>
    public void Free(in SlabAllocation allocation)
    {
        switch (allocation.Owner)
        {
            case Slab slab:
                RegionHandle region = new(slab, allocation.Index);
                if (_disposed) slab.Bin.FreeBatch(new ReadOnlySpan<RegionHandle>(in region));
                else _threadCache.Value!.Return(slab.Bin.ClassIndex, in region);
                break;
            case SlabChunk chunk:
                FreeLarge(chunk, allocation.Index, allocation.Capacity);
                break;
            default:
                NativeMemory.Free(allocation.Pointer);
                GC.RemoveMemoryPressure(allocation.Capacity);
                Interlocked.Add(ref _hugeBytes, -allocation.Capacity);
                break;
        }
    }

    /// <summary>Returns the calling thread's cached regions to the bins, so an idle thread pins nothing.</summary>
    public void FlushThreadCache()
    {
        if (_threadCache.IsValueCreated) _threadCache.Value!.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        FlushThreadCache();
        _disposed = true;
        foreach (SizeClassBin bin in _bins) bin.ReleaseSpare();
        lock (_pageLock)
        {
            for (int i = _chunks.Count - 1; i >= 0; i--)
            {
                if (!_chunks[i].IsEmpty) continue;
                _chunks[i].Release();
                _chunks.RemoveAt(i);
            }
        }

        _threadCache.Dispose();
    }

    internal Slab CreateSlab(SizeClassBin bin)
    {
        lock (_pageLock)
        {
            SlabChunk chunk = AllocatePages(bin.SlabOrder, out int firstPage);
            return new Slab(bin, chunk, firstPage, bin.SlabOrder, bin.ClassSize, bin.RegionCount);
        }
    }

    internal void ReleaseSlab(Slab slab)
    {
        lock (_pageLock) FreePages(slab.Chunk, slab.FirstPage, slab.Order);
    }

    private int ClassIndex(int size) => size == 0 ? 0 : _classOfQuantum[(size - 1) / _options.Quantum];

    private int LargeOrder(int size) => BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)((size + _options.PageSize - 1) / _options.PageSize)));

    private SlabAllocation AllocateLarge(int order)
    {
        lock (_pageLock)
        {
            SlabChunk chunk = AllocatePages(order, out int firstPage);
            int capacity = _options.PageSize << order;
            _largeBytes += capacity;
            return new SlabAllocation(chunk.PagePointer(firstPage), capacity, chunk, firstPage);
        }
    }

    private void FreeLarge(SlabChunk chunk, int firstPage, int capacity)
    {
        lock (_pageLock)
        {
            _largeBytes -= capacity;
            FreePages(chunk, firstPage, BitOperations.Log2((uint)(capacity / _options.PageSize)));
        }
    }

    private SlabChunk AllocatePages(int order, out int firstPage)
    {
        foreach (SlabChunk candidate in _chunks)
        {
            firstPage = candidate.TryAllocatePages(order);
            if (firstPage >= 0) return candidate;
        }

        SlabChunk chunk = new(_options.ChunkSize, _options.PageSize);
        _chunks.Add(chunk);
        firstPage = chunk.TryAllocatePages(order);
        return chunk;
    }

    /// <remarks>One empty chunk is kept spare while the allocator is live; a second one, or any once disposed, is released.</remarks>
    private void FreePages(SlabChunk chunk, int firstPage, int order)
    {
        chunk.FreePages(firstPage, order);
        if (!chunk.IsEmpty) return;
        if (!_disposed)
        {
            foreach (SlabChunk other in _chunks)
            {
                if (!ReferenceEquals(other, chunk) && other.IsEmpty) goto Release;
            }

            return;
        }

    Release:
        _chunks.Remove(chunk);
        chunk.Release();
    }

    private static int ChooseSlabOrder(int classSize, int pageSize, int chunkMaxOrder)
    {
        int bestOrder = -1;
        double bestWasteFraction = double.MaxValue;
        for (int order = 0; order <= chunkMaxOrder; order++)
        {
            long run = (long)pageSize << order;
            if (run < classSize) continue;
            long waste = run % classSize;
            if (waste * SlabTailWasteDenominator <= run) return order;
            double wasteFraction = (double)waste / run;
            if (wasteFraction < bestWasteFraction)
            {
                bestOrder = order;
                bestWasteFraction = wasteFraction;
            }

            if (order >= PreferredMaxSlabOrder) break;
        }

        return bestOrder;
    }
}
