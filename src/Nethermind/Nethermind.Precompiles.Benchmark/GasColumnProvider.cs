// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Nethermind.Specs.Forks;
using BenchmarkDotNet.Mathematics;
using Perfolizer.Mathematics.Common;

namespace Nethermind.Precompiles.Benchmark;

public class GasColumnProvider : IColumnProvider
{
    private static readonly IColumn[] Columns = [
        new GasColumn(),
        new GasThroughputColumn(),
        new GasConfidenceIntervalColumn(true),  // Lower bound
        new GasConfidenceIntervalColumn(false)  // Upper bound
    ];

    public IEnumerable<IColumn> GetColumns(Summary summary) => Columns;

    private abstract class BaseGasColumn : IColumn
    {
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Size;

        public abstract string Id { get; }
        public abstract string ColumnName { get; }
        public abstract string Legend { get; }

        protected static (long? gas, Statistics? stats) GetBenchmarkData(Summary summary, BenchmarkCase benchmarkCase)
        {
            BenchmarkDotNet.Parameters.ParameterInstance? inputParam = benchmarkCase.Parameters.Items.FirstOrDefault(p => p.Name == "Input");
            var gas = ((PrecompileBenchmarkBase.Param)inputParam!.Value).Gas(Cancun.Instance);
            Statistics? stats = summary.Reports.FirstOrDefault(r => r.BenchmarkCase == benchmarkCase)?.ResultStatistics;
            return (gas, stats);
        }

        protected static double CalculateMGasThroughput(long gas, double nanoseconds)
        {
            double opThroughput = 1_000_000_000.0 / nanoseconds;
            return gas * opThroughput / 1_000_000.0;
        }

        public abstract string GetValue(Summary summary, BenchmarkCase benchmarkCase);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
            => GetValue(summary, benchmarkCase);

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public bool IsAvailable(Summary summary) => true;
    }

    private class GasColumn : BaseGasColumn
    {
        public override string Id => "Gas";
        public override string ColumnName => "Gas";
        public override string Legend => "Amount of gas used by the operation";

        public override string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            (long? gas, Statistics? _) = GetBenchmarkData(summary, benchmarkCase);
            return gas?.ToString() ?? "N/A";
        }
    }

    private class GasThroughputColumn : BaseGasColumn
    {
        public override string Id => "GasThroughput";
        public override string ColumnName => "Throughput";
        public override string Legend => "Amount of gas processed per second";

        public override string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            (long? gas, Statistics? stats) = GetBenchmarkData(summary, benchmarkCase);

            if (gas is null || stats?.Mean is null)
            {
                return "N/A";
            }

            double mgasThroughput = CalculateMGasThroughput(gas.Value, stats.Mean);
            return mgasThroughput.ToString("F2") + " MGas/s";
        }
    }

    private class GasConfidenceIntervalColumn(bool isLower) : BaseGasColumn
    {
        public override string Id => isLower ? "GasCI-Lower" : "GasCI-Upper";
        public override string ColumnName => isLower ? "Throughput CI-Lower" : "Throughput CI-Upper";
        public override string Legend => $"{(isLower ? "Lower" : "Upper")} bound of gas throughput 99% confidence interval";

        public override string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            (long? gas, Statistics? stats) = GetBenchmarkData(summary, benchmarkCase);

            if (gas is null || stats?.Mean is null)
            {
                return "N/A";
            }

            ConfidenceInterval ci = stats.GetConfidenceInterval(ConfidenceLevel.L99);
            double bound = isLower ? ci.Lower : ci.Upper;
            double mgasThroughput = CalculateMGasThroughput(gas.Value, bound);
            return mgasThroughput.ToString("F2") + " MGas/s";
        }
    }
}
