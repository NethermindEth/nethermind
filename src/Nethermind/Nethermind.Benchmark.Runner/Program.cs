// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Linq;
using BenchmarkDotNet.Columns;
using Nethermind.Merge.Plugin.Benchmark;
using Nethermind.Precompiles.Benchmark;

namespace Nethermind.Benchmark.Runner
{
    public class DashboardConfig : ManualConfig
    {
        public DashboardConfig(params Job[] jobs)
        {
            //foreach (Job job in jobs)
            //{
            //    AddJob(job.WithToolchain(InProcessNoEmitToolchain.Instance));
            //}

            AddColumnProvider(DefaultColumnProviders.Descriptor);
            AddColumnProvider(DefaultColumnProviders.Statistics);
            AddColumnProvider(DefaultColumnProviders.Params);
            AddColumnProvider(DefaultColumnProviders.Metrics);
            AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
            AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
            WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(100));
            BuildTimeout = TimeSpan.FromMinutes(10);
        }
    }

    public class PrecompileBenchmarkConfig : DashboardConfig
    {
        public PrecompileBenchmarkConfig(Job job) : base(job) =>
            AddColumnProvider(new GasColumnProvider());
    }

    public static class Program
    {
        public static void Main(string[] args)
        {
            // Fail-fast correctness gate for every batch-keccak backend; must pass before trusting any timing.
            if (args.Contains("--verify-keccak-batch"))
            {
                Benchmarks.Core.KeccakBatchBackendVerifier.Run();
                return;
            }

            // Manual contention probe (CPU-cost-per-batch and iGPU-vs-saturator interference) runs outside BenchmarkDotNet.
            if (args.Contains("--contention-probe"))
            {
                Benchmarks.Core.ContentionProbe.Run();
                return;
            }

            bool quickMode = args.Contains("--quick");
            string[] benchmarkArgs = args.Where(static arg => arg != "--quick").ToArray();
            Job benchmarkJob = (quickMode ? Job.ShortRun : Job.MediumRun).WithRuntime(CoreRuntime.Core10_0);

            List<Assembly> additionalJobAssemblies = [
                typeof(JsonRpc.Benchmark.EthModuleBenchmarks).Assembly,
                typeof(Benchmarks.Core.Keccak256Benchmarks).Assembly,
                typeof(Evm.Benchmark.EvmStackBenchmarks).Assembly,
                typeof(Network.Benchmarks.DiscoveryBenchmarks).Assembly,
                typeof(NewPayloadSerializationBenchmarks).Assembly,
            ];

            List<Assembly> simpleJobAssemblies = [
                // typeof(EthereumTests.Benchmark.EthereumTests).Assembly,
            ];

            if (Debugger.IsAttached)
            {
                BenchmarkSwitcher.FromAssemblies(additionalJobAssemblies.Union(simpleJobAssemblies).ToArray()).RunAll(new DebugInProcessConfig());
            }
            else
            {
                Assembly[] releaseAssemblies = additionalJobAssemblies
                    .Union(simpleJobAssemblies)
                    .Append(typeof(KeccakBenchmark).Assembly)
                    .Distinct()
                    .ToArray();

                BenchmarkSwitcher
                    .FromAssemblies(releaseAssemblies)
                    .Run(benchmarkArgs, new PrecompileBenchmarkConfig(benchmarkJob));
            }
        }
    }
}
