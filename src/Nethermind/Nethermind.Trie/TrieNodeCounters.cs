// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Trie;

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

    internal static long TotalHashCalculations => Sum(static tally => Volatile.Read(ref tally.HashCalculations));
    internal static long TotalRlpEncodings => Sum(static tally => Volatile.Read(ref tally.RlpEncodings));
    internal static long TotalRlpDecodings => Sum(static tally => Volatile.Read(ref tally.RlpDecodings));
    internal static long TotalLoadedFromDb => Sum(static tally => Volatile.Read(ref tally.LoadedFromDb));
    internal static long TotalLoadedFromCache => Sum(static tally => Volatile.Read(ref tally.LoadedFromCache));

    private static long Sum(Func<Tally, long> select)
    {
        long total = 0;
        foreach (Tally tally in _tallies)
        {
            total += select(tally);
        }

        return total;
    }

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
