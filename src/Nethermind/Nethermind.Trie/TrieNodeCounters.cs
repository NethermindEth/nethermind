// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Trie;

/// <summary>
/// Per-thread tallies for the counters that tick once per trie node, summed on read.
/// </summary>
/// <remarks>
/// These are incremented once per node hashed, encoded, decoded or loaded, from the commit fan-out, the
/// prewarmer and the trie warmer at the same time — tens of thousands of times per block across many
/// threads. A shared interlocked counter makes that loop pay an atomic per node, which measurably caps
/// its throughput.
/// <para>
/// The tally lives in a <see cref="ThreadStaticAttribute"/> field, so an increment is a thread-local
/// load, a null check and a plain add. Two alternatives measured worse and are recorded here so they are
/// not retried: spreading the atomic over 64 cache lines recovered only ~0.04 ms per block (the atomic
/// itself is the cost, not the line transfer), and holding the tally in a <see cref="ThreadLocal{T}"/>
/// was ~0.4 ms per block WORSE than the shared counter, because its <c>Value</c> lookup costs more than
/// the atomic it replaces.
/// </para>
/// <para>
/// Totals stay exact: tallies are registered on creation and never removed, so a thread ending keeps its
/// counts. Reads are per-field, so a scrape can miss an increment that has not been published yet — the
/// same window a shared interlocked counter has.
/// </para>
/// </remarks>
internal static class TrieNodeCounters
{
    private static readonly ConcurrentBag<Tally> _tallies = [];

    [ThreadStatic]
    private static Tally? _threadTally;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Tally Current() => _threadTally ?? CreateTally();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Tally CreateTally()
    {
        Tally tally = new();
        _tallies.Add(tally);
        _threadTally = tally;
        return tally;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementHashCalculations() => Current().HashCalculations++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementRlpEncodings() => Current().RlpEncodings++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementRlpDecodings() => Current().RlpDecodings++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementLoadedFromDb() => Current().LoadedFromDb++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementLoadedFromCache() => Current().LoadedFromCache++;

    internal static long TotalHashCalculations => Sum(static t => t.HashCalculations);
    internal static long TotalRlpEncodings => Sum(static t => t.RlpEncodings);
    internal static long TotalRlpDecodings => Sum(static t => t.RlpDecodings);
    internal static long TotalLoadedFromDb => Sum(static t => t.LoadedFromDb);
    internal static long TotalLoadedFromCache => Sum(static t => t.LoadedFromCache);

    private static long Sum(Func<Tally, long> select)
    {
        long total = 0;
        foreach (Tally tally in _tallies)
        {
            total += select(tally);
        }

        return total;
    }

    /// <summary>
    /// One instance per thread. The counters share a line on purpose — one thread owns them all — while
    /// the trailing padding keeps a neighbouring thread's tally off that line.
    /// </summary>
    private sealed class Tally
    {
        public long HashCalculations;
        public long RlpEncodings;
        public long RlpDecodings;
        public long LoadedFromDb;
        public long LoadedFromCache;

#pragma warning disable CS0169, IDE0051 // padding against false sharing with an adjacent allocation
        private readonly long _pad0;
        private readonly long _pad1;
        private readonly long _pad2;
        private readonly long _pad3;
        private readonly long _pad4;
        private readonly long _pad5;
        private readonly long _pad6;
        private readonly long _pad7;
        private readonly long _pad8;
        private readonly long _pad9;
        private readonly long _pad10;
        private readonly long _pad11;
#pragma warning restore CS0169, IDE0051
    }
}
