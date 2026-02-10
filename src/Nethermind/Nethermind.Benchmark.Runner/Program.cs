// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Linq;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using BenchmarkDotNet.Columns;
using Nethermind.Precompiles.Benchmark;

namespace Nethermind.Benchmark.Runner
{
    public class DashboardConfig : ManualConfig
    {
        public DashboardConfig(params Job[] jobs)
        {
            foreach (Job job in jobs)
            {
                AddJob(job.WithToolchain(InProcessNoEmitToolchain.Instance));
            }

            AddColumnProvider(DefaultColumnProviders.Descriptor);
            AddColumnProvider(DefaultColumnProviders.Statistics);
            AddColumnProvider(DefaultColumnProviders.Params);
            AddColumnProvider(DefaultColumnProviders.Metrics);
            AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
            AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
            AddExporter(CsvExporter.Default);
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
            WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(100));
        }
    }

    public class PrecompileBenchmarkConfig : DashboardConfig
    {
        public PrecompileBenchmarkConfig(Job job) : base(job)
        {
            AddColumnProvider(new GasColumnProvider());
        }
    }

    public static class Program
    {
        public static void Main(string[] args)
        {
            Job selectedJob = ResolveJob();

            List<Assembly> additionalJobAssemblies = [
                typeof(JsonRpc.Benchmark.EthModuleBenchmarks).Assembly,
                typeof(Benchmarks.Core.Keccak256Benchmarks).Assembly,
                typeof(Evm.Benchmark.EvmStackBenchmarks).Assembly,
                typeof(Network.Benchmarks.DiscoveryBenchmarks).Assembly,
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
                foreach (Assembly assembly in additionalJobAssemblies)
                {
                    BenchmarkRunner.Run(assembly, new DashboardConfig(selectedJob), args);
                }

                foreach (Assembly assembly in simpleJobAssemblies)
                {
                    BenchmarkRunner.Run(assembly, new DashboardConfig(selectedJob), args);
                }

                if (ShouldRunPrecompiles())
                {
                    BenchmarkRunner.Run(typeof(KeccakBenchmark).Assembly, new PrecompileBenchmarkConfig(selectedJob), args);
                }
                else
                {
                    Console.WriteLine("Skipping precompile benchmarks. Set NETH_BENCHMARK_INCLUDE_PRECOMPILES=true to include them.");
                }
            }
        }

        private static Job ResolveJob()
        {
            string configuredJob = Environment.GetEnvironmentVariable("NETH_BENCHMARK_JOB");
            return configuredJob?.Trim().ToLowerInvariant() switch
            {
                "dry" => Job.Dry,
                "short" => Job.ShortRun,
                "long" => Job.LongRun,
                _ => Job.MediumRun
            };
        }

        private static bool ShouldRunPrecompiles()
        {
            string includePrecompiles = Environment.GetEnvironmentVariable("NETH_BENCHMARK_INCLUDE_PRECOMPILES");
            return includePrecompiles is not null && (
                includePrecompiles.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                includePrecompiles.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
    }
}
