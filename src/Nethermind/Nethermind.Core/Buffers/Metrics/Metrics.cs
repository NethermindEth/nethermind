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

    [GaugeMetric]
    [Description("Number of active pooled RefCountingMemory instances")]
    public static long ActivePooledRefCountingMemoryCount => Volatile.Read(ref _activePooledRefCountingMemoryCount);

    [GaugeMetric]
    [Description("Total backing-array capacity of active pooled RefCountingMemory instances in bytes")]
    public static long ActivePooledRefCountingMemoryCapacity => Volatile.Read(ref _activePooledRefCountingMemoryCapacity);

    [GaugeMetric]
    [Description("Number of active non-pooled RefCountingMemory instances")]
    public static long ActiveNonPooledRefCountingMemoryCount => Volatile.Read(ref _activeNonPooledRefCountingMemoryCount);

    internal static void ReportRefCountingMemoryAllocation(bool pooled, int capacity)
    {
        if (pooled)
        {
            Interlocked.Increment(ref _activePooledRefCountingMemoryCount);
            Interlocked.Add(ref _activePooledRefCountingMemoryCapacity, capacity);
        }
        else
        {
            Interlocked.Increment(ref _activeNonPooledRefCountingMemoryCount);
        }
    }

    internal static void ReportRefCountingMemoryRelease(bool pooled, int capacity)
    {
        if (pooled)
        {
            Interlocked.Decrement(ref _activePooledRefCountingMemoryCount);
            Interlocked.Add(ref _activePooledRefCountingMemoryCapacity, -capacity);
        }
        else
        {
            Interlocked.Decrement(ref _activeNonPooledRefCountingMemoryCount);
        }
    }
}
