// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Nethermind.Evm.Benchmark;

public sealed class EvmOpcodeGasColumnProvider : IColumnProvider
{
    private static readonly IColumn[] Columns =
    [
        new OpcodeGasColumn(),
        new OpcodeGasThroughputColumn(),
        new OpcodeThresholdColumn(),
    ];

    private static readonly ConcurrentDictionary<Instruction, long?> GasByOpcode = new();

    public IEnumerable<IColumn> GetColumns(Summary summary) => Columns;

    private abstract class BaseOpcodeGasColumn : IColumn
    {
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Size;

        public abstract string Id { get; }
        public abstract string ColumnName { get; }
        public abstract string Legend { get; }

        protected static bool TryGetBenchmarkData(
            Summary summary,
            BenchmarkCase benchmarkCase,
            out long gas,
            out Statistics stats)
        {
            gas = 0;
            stats = summary.Reports.FirstOrDefault(r => r.BenchmarkCase == benchmarkCase)?.ResultStatistics;

            if (benchmarkCase.Descriptor.Type != typeof(EvmOpcodesBenchmark))
            {
                return false;
            }

            object opcodeValue = benchmarkCase.Parameters.Items
                .FirstOrDefault(p => p.Name == nameof(EvmOpcodesBenchmark.Opcode))
                ?.Value;

            if (opcodeValue is not Instruction opcode)
            {
                return false;
            }

            long? estimatedGas = GasByOpcode.GetOrAdd(
                opcode,
                static op => EvmOpcodesBenchmark.TryEstimateOpcodeGas(op, out long measuredGas) ? measuredGas : null);

            if (estimatedGas is null)
            {
                return false;
            }

            gas = estimatedGas.Value;
            return true;
        }

        public abstract string GetValue(Summary summary, BenchmarkCase benchmarkCase);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
            GetValue(summary, benchmarkCase);

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public bool IsAvailable(Summary summary) =>
            summary.BenchmarksCases.Any(static c => c.Descriptor.Type == typeof(EvmOpcodesBenchmark));
    }

    private sealed class OpcodeGasColumn : BaseOpcodeGasColumn
    {
        public override string Id => "OpcodeGas";
        public override string ColumnName => "Gas";
        public override string Legend => "Gas consumed by the configured opcode setup";

        public override string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            return TryGetBenchmarkData(summary, benchmarkCase, out long gas, out _)
                ? gas.ToString(CultureInfo.InvariantCulture)
                : "N/A";
        }
    }

    private sealed class OpcodeGasThroughputColumn : BaseOpcodeGasColumn
    {
        public override string Id => "OpcodeMGasPerSec";
        public override string ColumnName => "MGas/s";
        public override string Legend => "Gas throughput derived from Mean and Gas";

        public override string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            if (!TryGetBenchmarkData(summary, benchmarkCase, out long gas, out Statistics stats) ||
                stats?.Mean is null ||
                stats.Mean <= 0)
            {
                return "N/A";
            }

            double mGasPerSecond = gas * 1_000.0 / stats.Mean;
            return mGasPerSecond.ToString("F2", CultureInfo.InvariantCulture);
        }
    }

    private sealed class OpcodeThresholdColumn : IColumn
    {
        public string Id => "OpcodeThreshold";
        public string ColumnName => "Threshold";
        public string Legend => "Regression detection threshold (%) for this opcode category";
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 1;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            if (benchmarkCase.Descriptor.Type != typeof(EvmOpcodesBenchmark))
            {
                return "N/A";
            }

            object opcodeValue = benchmarkCase.Parameters.Items
                .FirstOrDefault(p => p.Name == nameof(EvmOpcodesBenchmark.Opcode))
                ?.Value;

            if (opcodeValue is not Instruction opcode)
            {
                return "N/A";
            }

            return EvmOpcodesBenchmark.GetThresholdPercent(opcode)
                .ToString("F1", CultureInfo.InvariantCulture);
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
            GetValue(summary, benchmarkCase);

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public bool IsAvailable(Summary summary) =>
            summary.BenchmarksCases.Any(static c => c.Descriptor.Type == typeof(EvmOpcodesBenchmark));
    }
}
