// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture]
public class StopwatchExtensionsTests
{
    // On Linux, Stopwatch.Frequency is 1_000_000_000 (nanosecond ticks), so `ticks * 1_000_000`
    // overflows long once ticks exceeds ~9.22e12 (~2h33m of elapsed time).
    [TestCase(10_000_000_000_000L, 1_000_000_000L, 10_000_000_000L)] // ~2h46m of 1GHz ticks: overflows the naive `ticks * 1_000_000` formula
    [TestCase(long.MaxValue, 1_000_000_000L, 9_223_372_036_854_775L)] // largest possible tick count: the naive formula wraps clean through zero here
    [TestCase(123_456_789L, 10_000_000L, 12_345_678L)] // Windows-style 10MHz QPC frequency, non-multiple ticks
    [TestCase(1_500L, 1_000_000_000L, 1L)] // sub-microsecond precision truncates rather than rounds
    [TestCase(0L, 1_000_000_000L, 0L)] // zero elapsed ticks
    public void ToMicroseconds_computes_elapsed_microseconds_without_overflow(long ticks, long frequency, long expected) =>
        Assert.That(StopwatchExtensions.ToMicroseconds(ticks, frequency), Is.EqualTo(expected));

    [Test]
    public void ElapsedMicroseconds_matches_stopwatch_elapsed()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Thread.Sleep(5);
        stopwatch.Stop();

        long microseconds = stopwatch.ElapsedMicroseconds();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(microseconds, Is.GreaterThanOrEqualTo(0));
            Assert.That((double)microseconds, Is.EqualTo(stopwatch.Elapsed.TotalMicroseconds).Within(1));
        }
    }
}
