// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>Geometry of a <see cref="SlabMemoryAllocator"/>: the slab page size, the small size classes, and the thread-cache depth.</summary>
/// <remarks>
/// A request up to the largest size class is served from a slab of that class, and a larger one gets
/// a dedicated native allocation. Every class must be a multiple of <see cref="Quantum"/>, which keeps
/// regions 16-byte aligned off the malloc-aligned slab base without needing an aligned allocation.
/// </remarks>
public sealed class SlabAllocatorOptions
{
    public const int MinimumQuantum = 16;
    public const int DefaultPageSize = 16 * 1024;
    public const int DefaultQuantum = 64;
    public const int DefaultThreadCacheMaxCount = 32;

    private readonly int[] _sizeClasses;

    /// <param name="sizeClasses">Strictly ascending small size classes, each a multiple of <paramref name="quantum"/>.</param>
    /// <param name="pageSize">Granularity of a slab's size in bytes; a power of two.</param>
    /// <param name="quantum">Size-class granularity in bytes; a power of two of at least <see cref="MinimumQuantum"/>.</param>
    /// <param name="threadCacheMaxCount">Upper bound on regions a thread caches per class; a class caps lower so a cache holds at most ~64 KiB of it.</param>
    public SlabAllocatorOptions(int[] sizeClasses, int pageSize, int quantum, int threadCacheMaxCount)
    {
        ArgumentNullException.ThrowIfNull(sizeClasses);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantum, MinimumQuantum);
        if (!BitOperations.IsPow2(quantum)) throw new ArgumentOutOfRangeException(nameof(quantum), quantum, "must be a power of two");
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, quantum);
        if (!BitOperations.IsPow2(pageSize)) throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "must be a power of two");
        ArgumentOutOfRangeException.ThrowIfZero(sizeClasses.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCacheMaxCount, 1);

        int previous = 0;
        foreach (int sizeClass in sizeClasses)
        {
            if (sizeClass <= previous || sizeClass % quantum != 0)
                throw new ArgumentOutOfRangeException(nameof(sizeClasses), sizeClass, $"classes must ascend in multiples of {quantum}");
            previous = sizeClass;
        }

        _sizeClasses = (int[])sizeClasses.Clone();
        PageSize = pageSize;
        Quantum = quantum;
        ThreadCacheMaxCount = threadCacheMaxCount;
    }

    public int PageSize { get; }
    public int Quantum { get; }
    public ReadOnlySpan<int> SizeClasses => _sizeClasses;
    public int ThreadCacheMaxCount { get; }

    /// <summary>
    /// Generates jemalloc-style size classes: <paramref name="quantum"/> apart while a doubling is
    /// too small to hold <paramref name="classesPerDoubling"/> of them, then that many evenly spaced
    /// classes per doubling, up to <paramref name="maxSize"/> inclusive.
    /// </summary>
    /// <remarks>
    /// With quantum 64 and four classes per doubling the spacing above 1024 is 256, so any request
    /// wastes under a quarter of its class and typically far less (1200 bytes lands in 1280).
    /// </remarks>
    public static int[] GenerateSizeClasses(int quantum, int classesPerDoubling, int maxSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantum, MinimumQuantum);
        if (!BitOperations.IsPow2(quantum)) throw new ArgumentOutOfRangeException(nameof(quantum), quantum, "must be a power of two");
        if (!BitOperations.IsPow2(classesPerDoubling)) throw new ArgumentOutOfRangeException(nameof(classesPerDoubling), classesPerDoubling, "must be a power of two");
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, quantum);

        List<int> classes = [];
        for (long groupBase = quantum; groupBase <= maxSize; groupBase *= 2)
        {
            long spacing = Math.Max(quantum, groupBase / classesPerDoubling);
            for (long size = groupBase; size < groupBase * 2 && size <= maxSize; size += spacing)
                classes.Add((int)size);
        }

        return classes.ToArray();
    }
}
