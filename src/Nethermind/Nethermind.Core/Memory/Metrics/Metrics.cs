// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;

namespace Nethermind.Core.Memory.Metrics;

public static class Metrics
{
    private static long _activePooledRefCountingMemoryCount;
    private static long _activePooledRefCountingMemoryCapacity;
    private static long _activeRocksDbRefCountingMemoryCount;
    private static long _activeRocksDbRefCountingMemoryCapacity;
    private static long _activeNativeRefCountingMemoryCount;
    private static long _activeNativeRefCountingMemoryCapacity;

    [GaugeMetric]
    [Description("Number of active pooled RefCountingMemory instances")]
    public static long ActivePooledRefCountingMemoryCount => Volatile.Read(ref _activePooledRefCountingMemoryCount);

    [GaugeMetric]
    [Description("Total backing-array capacity of active pooled RefCountingMemory instances in bytes")]
    public static long ActivePooledRefCountingMemoryCapacity => Volatile.Read(ref _activePooledRefCountingMemoryCapacity);

    [GaugeMetric]
    [Description("Number of active RocksDB RefCountingMemory instances")]
    public static long ActiveRocksDbRefCountingMemoryCount => Volatile.Read(ref _activeRocksDbRefCountingMemoryCount);

    [GaugeMetric]
    [Description("Total capacity of active RocksDB RefCountingMemory instances in bytes")]
    public static long ActiveRocksDbRefCountingMemoryCapacity => Volatile.Read(ref _activeRocksDbRefCountingMemoryCapacity);

    [GaugeMetric]
    [Description("Number of active native-pooled RefCountingMemory instances")]
    public static long ActiveNativeRefCountingMemoryCount => Volatile.Read(ref _activeNativeRefCountingMemoryCount);

    [GaugeMetric]
    [Description("Total bucket capacity of active native-pooled RefCountingMemory instances in bytes")]
    public static long ActiveNativeRefCountingMemoryCapacity => Volatile.Read(ref _activeNativeRefCountingMemoryCapacity);

    internal static void ReportRefCountingMemoryAllocation(RefCountingMemory.BackingKind backingKind, int capacity) =>
        Report(backingKind, 1, capacity);

    internal static void ReportRefCountingMemoryRelease(RefCountingMemory.BackingKind backingKind, int capacity) =>
        Report(backingKind, -1, -capacity);

    private static void Report(RefCountingMemory.BackingKind backingKind, int count, int capacity)
    {
        switch (backingKind)
        {
            case RefCountingMemory.BackingKind.Pooled:
                Interlocked.Add(ref _activePooledRefCountingMemoryCount, count);
                Interlocked.Add(ref _activePooledRefCountingMemoryCapacity, capacity);
                break;
            case RefCountingMemory.BackingKind.RocksDb:
                Interlocked.Add(ref _activeRocksDbRefCountingMemoryCount, count);
                Interlocked.Add(ref _activeRocksDbRefCountingMemoryCapacity, capacity);
                break;
            case RefCountingMemory.BackingKind.Native:
                Interlocked.Add(ref _activeNativeRefCountingMemoryCount, count);
                Interlocked.Add(ref _activeNativeRefCountingMemoryCapacity, capacity);
                break;
        }
    }
}
