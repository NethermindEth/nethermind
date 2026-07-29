// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;

namespace Nethermind.Core.Buffers.Metrics;

public static class Metrics
{
    private static long _activePooledRefCountingMemoryCount;
    private static long _activePooledRefCountingMemoryCapacity;
    private static long _activeNonPooledRefCountingMemoryCount;
    private static long _activeRocksDbRefCountingMemoryCount;
    private static long _activeRocksDbRefCountingMemoryCapacity;

    [GaugeMetric]
    [Description("Number of active pooled RefCountingMemory instances")]
    public static long ActivePooledRefCountingMemoryCount => Volatile.Read(ref _activePooledRefCountingMemoryCount);

    [GaugeMetric]
    [Description("Total backing-array capacity of active pooled RefCountingMemory instances in bytes")]
    public static long ActivePooledRefCountingMemoryCapacity => Volatile.Read(ref _activePooledRefCountingMemoryCapacity);

    [GaugeMetric]
    [Description("Number of active non-pooled RefCountingMemory instances")]
    public static long ActiveNonPooledRefCountingMemoryCount => Volatile.Read(ref _activeNonPooledRefCountingMemoryCount);

    [GaugeMetric]
    [Description("Number of active RocksDB RefCountingMemory instances")]
    public static long ActiveRocksDbRefCountingMemoryCount => Volatile.Read(ref _activeRocksDbRefCountingMemoryCount);

    [GaugeMetric]
    [Description("Total capacity of active RocksDB RefCountingMemory instances in bytes")]
    public static long ActiveRocksDbRefCountingMemoryCapacity => Volatile.Read(ref _activeRocksDbRefCountingMemoryCapacity);

    internal static void ReportRefCountingMemoryAllocation(RefCountingMemory.BackingKind backingKind, int capacity)
    {
        switch (backingKind)
        {
            case RefCountingMemory.BackingKind.Pooled:
                Interlocked.Increment(ref _activePooledRefCountingMemoryCount);
                Interlocked.Add(ref _activePooledRefCountingMemoryCapacity, capacity);
                break;
            case RefCountingMemory.BackingKind.Wrapped:
                Interlocked.Increment(ref _activeNonPooledRefCountingMemoryCount);
                break;
            case RefCountingMemory.BackingKind.RocksDb:
                Interlocked.Increment(ref _activeRocksDbRefCountingMemoryCount);
                Interlocked.Add(ref _activeRocksDbRefCountingMemoryCapacity, capacity);
                break;
        }
    }

    internal static void ReportRefCountingMemoryRelease(RefCountingMemory.BackingKind backingKind, int capacity)
    {
        switch (backingKind)
        {
            case RefCountingMemory.BackingKind.Pooled:
                Interlocked.Decrement(ref _activePooledRefCountingMemoryCount);
                Interlocked.Add(ref _activePooledRefCountingMemoryCapacity, -capacity);
                break;
            case RefCountingMemory.BackingKind.Wrapped:
                Interlocked.Decrement(ref _activeNonPooledRefCountingMemoryCount);
                break;
            case RefCountingMemory.BackingKind.RocksDb:
                Interlocked.Decrement(ref _activeRocksDbRefCountingMemoryCount);
                Interlocked.Add(ref _activeRocksDbRefCountingMemoryCapacity, -capacity);
                break;
        }
    }
}
