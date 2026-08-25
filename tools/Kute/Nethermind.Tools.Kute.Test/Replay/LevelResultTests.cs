// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.Replay;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test.Replay;

public class LevelResultTests
{
    private static LevelResult WithLatencies(int count, int failed = 0)
    {
        TimeSpan[] latencies = new TimeSpan[count];
        for (int i = 0; i < count; i++)
        {
            latencies[i] = TimeSpan.FromMilliseconds(i + 1);
        }

        return new LevelResult
        {
            Concurrency = 1,
            Succeeded = count - failed,
            RpcErrors = failed,
            HttpErrors = 0,
            TransportErrors = 0,
            Elapsed = TimeSpan.FromSeconds(2),
            RequestBytes = 0,
            Latencies = latencies,
            Rewritten = 0,
            FeesStripped = 0,
        };
    }

    [TestCase(0.0, 1, TestName = "Minimum is the fastest sample")]
    [TestCase(0.5, 50, TestName = "p50")]
    [TestCase(0.9, 90, TestName = "p90")]
    [TestCase(0.99, 99, TestName = "p99")]
    [TestCase(1.0, 100, TestName = "Maximum is the slowest sample")]
    public void Reports_nearest_rank_percentiles(double quantile, int expectedMilliseconds)
    {
        LevelResult result = WithLatencies(100);

        Assert.That(result.Percentile(quantile).TotalMilliseconds, Is.EqualTo(expectedMilliseconds));
    }

    [Test]
    public void Reports_percentiles_of_a_single_sample()
    {
        LevelResult result = WithLatencies(1);

        Assert.That(result.Min, Is.EqualTo(TimeSpan.FromMilliseconds(1)));
        Assert.That(result.P99, Is.EqualTo(TimeSpan.FromMilliseconds(1)));
        Assert.That(result.Max, Is.EqualTo(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public void Reports_zero_for_a_level_with_no_samples()
    {
        // A level that sent nothing must not divide by zero on its way to the report.
        LevelResult result = WithLatencies(0);

        Assert.That(result.Mean, Is.EqualTo(TimeSpan.Zero));
        Assert.That(result.P50, Is.EqualTo(TimeSpan.Zero));
        Assert.That(result.RequestsPerSecond, Is.Zero);
        Assert.That(result.FailureRate, Is.Zero);
    }

    [Test]
    public void Counts_failures_towards_throughput_but_reports_them_separately()
    {
        // Failed requests still consumed node time, so dropping them from the rate would overstate
        // how much work the node actually turned away.
        LevelResult result = WithLatencies(100, failed: 25);

        Assert.That(result.Total, Is.EqualTo(100));
        Assert.That(result.Failed, Is.EqualTo(25));
        Assert.That(result.Succeeded, Is.EqualTo(75));
        Assert.That(result.FailureRate, Is.EqualTo(0.25d));
        Assert.That(result.RequestsPerSecond, Is.EqualTo(50d));
    }

    [Test]
    public void Reports_the_mean_of_all_samples()
    {
        LevelResult result = WithLatencies(100);

        Assert.That(result.Mean.TotalMilliseconds, Is.EqualTo(50.5d).Within(0.001d));
    }
}
