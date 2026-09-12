// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Core.Extensions
{
    public static class StopwatchExtensions
    {
        public static long ElapsedMicroseconds(this Stopwatch stopwatch) => ToMicroseconds(stopwatch.ElapsedTicks, Stopwatch.Frequency);

        /// <remarks>
        /// Splitting <paramref name="ticks"/> into a whole-second part and a sub-second remainder keeps
        /// every intermediate value within <see langword="long"/> range, unlike
        /// <c>ticks * 1000000 / frequency</c>, which overflows once <paramref name="ticks"/> exceeds
        /// roughly <see langword="long"/>.MaxValue / 1000000 (about 2h33m at the 1GHz
        /// <see cref="Stopwatch.Frequency"/> on Linux). The result is exact for any
        /// <paramref name="frequency"/> of at least 1MHz; below that, the microsecond count no longer
        /// fits in a <see langword="long"/>.
        /// </remarks>
        internal static long ToMicroseconds(long ticks, long frequency) => ticks / frequency * 1000000 + ticks % frequency * 1000000 / frequency;
    }
}
