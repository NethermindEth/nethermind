// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Mathematics.Common;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

public class GasBenchmarkColumnProvider : IColumnProvider
{
    private static readonly IColumn[] Columns =
    [
        new MGasThroughputColumn(),
        new MGasConfidenceIntervalColumn(true),
        new MGasConfidenceIntervalColumn(false)
    ];

    public IEnumerable<IColumn> GetColumns(Summary summary) => Columns;

    private static double CalculateMGasThroughput(double nanoseconds)
    {
        // All gas-benchmark payloads use 100M gas
        const long gas = 100_000_000L;
        double opThroughput = 1_000_000_000.0 / nanoseconds;
        return gas * opThroughput / 1_000_000.0;
    }

    private class MGasThroughputColumn : IColumn
    {
        public string Id => "MGas/s";
        public string ColumnName => "MGas/s";
        public string Legend => "Throughput in millions of gas per second";
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Size;

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            Statistics stats = summary.Reports.FirstOrDefault(r => r.BenchmarkCase == benchmarkCase)?.ResultStatistics;
            if (stats is null)
                return "N/A";

            return CalculateMGasThroughput(stats.Mean).ToString("F2");
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
            => GetValue(summary, benchmarkCase);

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
        public bool IsAvailable(Summary summary) => true;
    }

    private class MGasConfidenceIntervalColumn(bool isLower) : IColumn
    {
        public string Id => isLower ? "CI-Lower" : "CI-Upper";
        public string ColumnName => isLower ? "CI-Lower" : "CI-Upper";
        public string Legend => $"{(isLower ? "Lower" : "Upper")} bound of MGas/s 99% confidence interval";
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Size;

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            Statistics stats = summary.Reports.FirstOrDefault(r => r.BenchmarkCase == benchmarkCase)?.ResultStatistics;
            if (stats is null)
                return "N/A";

            ConfidenceInterval ci = stats.GetConfidenceInterval(ConfidenceLevel.L99);
            double bound = isLower ? ci.Lower : ci.Upper;
            return CalculateMGasThroughput(bound).ToString("F2");
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
            => GetValue(summary, benchmarkCase);

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
        public bool IsAvailable(Summary summary) => true;
    }
}
