// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Reflection;
using BenchmarkDotNet.Columns;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

[TestFixture]
public class GasBenchmarkColumnProviderTests
{
    [Test]
    public void GetColumns_Returns_Three_Columns()
    {
        GasBenchmarkColumnProvider provider = new();

        IEnumerable<IColumn> columns = provider.GetColumns(null);

        List<IColumn> columnList = new(columns);
        Assert.That(columnList, Has.Count.EqualTo(3));
    }

    [Test]
    public void GetColumns_Contains_MGas_Column()
    {
        GasBenchmarkColumnProvider provider = new();

        List<IColumn> columnList = new(provider.GetColumns(null));

        Assert.That(columnList[0].ColumnName, Is.EqualTo("MGas/s"));
        Assert.That(columnList[0].Id, Is.EqualTo("MGas/s"));
        Assert.That(columnList[0].IsNumeric, Is.True);
        Assert.That(columnList[0].AlwaysShow, Is.True);
        Assert.That(columnList[0].Category, Is.EqualTo(ColumnCategory.Custom));
    }

    [Test]
    public void GetColumns_Contains_CI_Lower_Column()
    {
        GasBenchmarkColumnProvider provider = new();

        List<IColumn> columnList = new(provider.GetColumns(null));

        Assert.That(columnList[1].ColumnName, Is.EqualTo("CI-Lower"));
        Assert.That(columnList[1].Id, Is.EqualTo("CI-Lower"));
        Assert.That(columnList[1].IsNumeric, Is.True);
        Assert.That(columnList[1].Legend, Does.Contain("Lower"));
    }

    [Test]
    public void GetColumns_Contains_CI_Upper_Column()
    {
        GasBenchmarkColumnProvider provider = new();

        List<IColumn> columnList = new(provider.GetColumns(null));

        Assert.That(columnList[2].ColumnName, Is.EqualTo("CI-Upper"));
        Assert.That(columnList[2].Id, Is.EqualTo("CI-Upper"));
        Assert.That(columnList[2].IsNumeric, Is.True);
        Assert.That(columnList[2].Legend, Does.Contain("Upper"));
    }

    [Test]
    public void All_Columns_Are_Available()
    {
        GasBenchmarkColumnProvider provider = new();

        foreach (IColumn column in provider.GetColumns(null))
        {
            Assert.That(column.IsAvailable(null), Is.True);
        }
    }

    /// <summary>
    /// Verifies the MGas/s formula: 100M gas * (1e9 / mean_ns) / 1e6
    /// At 1 billion nanoseconds (1 second), throughput should be exactly 100 MGas/s.
    /// </summary>
    [TestCase(1_000_000_000.0, 100.0, Description = "1 second = 100 MGas/s")]
    [TestCase(500_000_000.0, 200.0, Description = "0.5 seconds = 200 MGas/s")]
    [TestCase(2_000_000_000.0, 50.0, Description = "2 seconds = 50 MGas/s")]
    [TestCase(100_000_000.0, 1000.0, Description = "0.1 seconds = 1000 MGas/s")]
    public void MGasThroughput_Formula_Is_Correct(double nanoseconds, double expectedMGasPerSecond)
    {
        // Use reflection to invoke the private static method
        MethodInfo calculateMethod = typeof(GasBenchmarkColumnProvider)
            .GetMethod("CalculateMGasThroughput", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(calculateMethod, Is.Not.Null, "CalculateMGasThroughput method must exist");

        double result = (double)calculateMethod.Invoke(null, [nanoseconds]);

        Assert.That(result, Is.EqualTo(expectedMGasPerSecond).Within(0.01));
    }
}
