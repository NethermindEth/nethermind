// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Mathematics;
using NUnit.Framework;
using Perfolizer.Mathematics.Common;

namespace Nethermind.Precompiles.Benchmark.Test;

public class GasColumnProviderTests
{
    [Test]
    public void Lower_throughput_bound_is_less_than_upper_throughput_bound()
    {
        long gas = 100_000;
        double[] samples = [900, 920, 950, 970, 980, 1000, 1000, 1010, 1020, 1050, 1080, 1100];
        Statistics stats = new(samples);

        double lowerThroughput = GasColumnProvider.GetThroughputBound(gas, stats, isLowerBound: true);
        double upperThroughput = GasColumnProvider.GetThroughputBound(gas, stats, isLowerBound: false);

        Assert.That(lowerThroughput, Is.LessThan(upperThroughput));
    }
}
