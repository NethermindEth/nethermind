// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Evm.State;

/// <summary>
/// Whether the backing-store reads of recent prewarm runs come from the disk, by the share of them slower than a
/// threshold, so finding a run's reads ahead and reading them side by side is worth what it costs.
/// </summary>
public static class ColdStore
{
    // Recent reads count fully: every DecayEvery reads, both counts halve.
    private const long DecayEvery = 4096;
    private const long MinReads = 256;

    /// <summary>Whether prewarm reads are timed at all; set once at startup.</summary>
    public static bool Observed { get; set; }

    private static long _reads;
    private static long _slowReads;
    private static long _slowTicks = Stopwatch.Frequency / 20_000; // 50 us

    /// <summary>A read is slow from this long; zero takes every read as cold.</summary>
    public static TimeSpan SlowRead
    {
        get => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _slowTicks));
        set => Volatile.Write(ref _slowTicks, (long)(value.TotalSeconds * Stopwatch.Frequency));
    }

    /// <summary>Records the time one backing-store read of a prewarm run took.</summary>
    public static void Observe(long elapsedTicks)
    {
        long slowTicks = Volatile.Read(ref _slowTicks);
        if (elapsedTicks >= slowTicks) Interlocked.Increment(ref _slowReads);
        if (Interlocked.Increment(ref _reads) % DecayEvery != 0) return;
        Interlocked.Exchange(ref _reads, Volatile.Read(ref _reads) / 2);
        Interlocked.Exchange(ref _slowReads, Volatile.Read(ref _slowReads) / 2);
    }

    /// <summary>Whether more than one recent read in twenty was slow; always with a zero threshold.</summary>
    public static bool IsCold
    {
        get
        {
            if (Volatile.Read(ref _slowTicks) == 0) return true;
            long reads = Volatile.Read(ref _reads);
            return reads >= MinReads && Volatile.Read(ref _slowReads) * 20 > reads;
        }
    }
}
