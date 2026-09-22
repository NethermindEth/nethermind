// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Core.Extensions
{
    public static class StopwatchExtensions
    {
        public static long ElapsedMicroseconds(this Stopwatch stopwatch) => ToMicroseconds(stopwatch.ElapsedTicks, Stopwatch.Frequency);

        /// <summary>Converts a raw <see cref="Stopwatch"/> tick count to whole microseconds, truncating any remainder.</summary>
        /// <remarks>
        /// Splitting <paramref name="ticks"/> into a whole-second part and a sub-second remainder keeps
        /// every intermediate value within <see langword="long"/> range, unlike
        /// <c>ticks * 1000000 / frequency</c>, which overflows once <paramref name="ticks"/> exceeds
        /// roughly <see langword="long"/>.MaxValue / 1000000 (about 2h33m at the 1GHz
        /// <see cref="Stopwatch.Frequency"/> on Linux). The split stays overflow-free for any
        /// <paramref name="ticks"/> up to <see langword="long"/>.MaxValue provided
        /// <paramref name="frequency"/> is at least 1MHz; the truncating division itself is exact
        /// for any positive <paramref name="frequency"/>.
        /// </remarks>
        internal static long ToMicroseconds(long ticks, long frequency) => ticks / frequency * 1000000 + ticks % frequency * 1000000 / frequency;
    }
}
