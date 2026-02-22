// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace Nethermind.Evm.Benchmark;

public static class Program
{
    public static void Main(string[] args)
    {
        var config = ManualConfig.CreateEmpty()
            .AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance))
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
            .AddExporter(JsonExporter.FullCompressed)
            .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);

        BenchmarkRunner.Run<TxProcessingBenchmark>(config, args);
    }
}
