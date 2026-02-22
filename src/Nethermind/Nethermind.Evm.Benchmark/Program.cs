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
        // TxProcessingBenchmark: state is restored via CallAndRestore so default unroll is fine.
        var txConfig = MakeBase().AddJob(
            Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));

        // BlockProcessingBenchmark: [IterationSetup] rebuilds world state once per iteration.
        // With the default UnrollFactor (16), BDN would invoke the benchmark 16 times per
        // iteration without re-running setup, leaving nonces/balances mutated on 2nd+ calls.
        // InvocationCount=1/UnrollFactor=1 ensures each invocation starts from a fresh state.
        var blockConfig = MakeBase().AddJob(
            Job.ShortRun
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));

        BenchmarkSwitcher.FromTypes([typeof(TxProcessingBenchmark)]).Run(args, txConfig);
        BenchmarkSwitcher.FromTypes([typeof(BlockProcessingBenchmark)]).Run(args, blockConfig);
    }

    // Other benchmarks in this project (EvmBenchmarks, EvmStackBenchmarks, etc.) can be run
    // via Nethermind.Benchmark.Runner or directly with: dotnet run -- --filter *EvmBenchmarks*

    private static ManualConfig MakeBase() => ManualConfig.CreateEmpty()
        .AddColumnProvider(DefaultColumnProviders.Instance)
        .AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
        .AddExporter(JsonExporter.FullCompressed)
        .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
}
