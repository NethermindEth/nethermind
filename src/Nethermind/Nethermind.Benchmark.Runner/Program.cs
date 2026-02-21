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
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Linq;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using BenchmarkDotNet.Columns;
using Nethermind.Precompiles.Benchmark;

namespace Nethermind.Benchmark.Runner
{
    public class FilteringLogger(ILogger innerLogger) : ILogger
    {
        public string Id => innerLogger.Id;

        public int Priority => innerLogger.Priority;

        public void Flush() => innerLogger.Flush();

        public void Write(LogKind logKind, string text)
        {
            if (ShouldSuppress(logKind, text))
            {
                return;
            }

            innerLogger.Write(logKind, text);
        }

        public void WriteLine() => innerLogger.WriteLine();

        public void WriteLine(LogKind logKind, string text)
        {
            if (ShouldSuppress(logKind, text))
            {
                return;
            }

            innerLogger.WriteLine(logKind, text);
        }

        private static bool ShouldSuppress(LogKind logKind, string text) =>
            logKind == LogKind.Warning &&
            !string.IsNullOrWhiteSpace(text) &&
            text.Contains("Failed to set up priority", StringComparison.OrdinalIgnoreCase);
    }

    public class DashboardConfig : ManualConfig
    {
        public DashboardConfig(bool useInProcessToolchain, params Job[] jobs)
        {
            foreach (Job job in jobs)
            {
                AddJob(useInProcessToolchain ? job.WithToolchain(InProcessNoEmitToolchain.Instance) : job);
            }

            AddColumnProvider(DefaultColumnProviders.Descriptor);
            AddColumnProvider(DefaultColumnProviders.Statistics);
            AddColumnProvider(DefaultColumnProviders.Params);
            AddColumnProvider(DefaultColumnProviders.Metrics);
            AddLogger(new FilteringLogger(ConsoleLogger.Default));
            AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
            AddExporter(CsvExporter.Default);
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
            WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(100));
        }
    }

    public class PrecompileBenchmarkConfig : DashboardConfig
    {
        public PrecompileBenchmarkConfig(bool useInProcessToolchain, Job job) : base(useInProcessToolchain, job)
        {
            AddColumnProvider(new GasColumnProvider());
        }
    }

    public static class Program
    {
        public static void Main(string[] args)
        {
            Job selectedJob = ResolveJob();
            bool useInProcessToolchain = ShouldUseInProcessToolchain();

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
                    BenchmarkRunner.Run(assembly, new DashboardConfig(useInProcessToolchain, selectedJob), args);
                }

                foreach (Assembly assembly in simpleJobAssemblies)
                {
                    BenchmarkRunner.Run(assembly, new DashboardConfig(useInProcessToolchain, selectedJob), args);
                }

                if (ShouldRunPrecompiles())
                {
                    BenchmarkRunner.Run(typeof(KeccakBenchmark).Assembly, new PrecompileBenchmarkConfig(useInProcessToolchain, selectedJob), args);
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

        private static bool ShouldUseInProcessToolchain()
        {
            string configuredToolchain = Environment.GetEnvironmentVariable("NETH_BENCHMARK_INPROCESS");
            if (string.IsNullOrWhiteSpace(configuredToolchain))
            {
                return true;
            }

            return configuredToolchain.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                configuredToolchain.Equals("true", StringComparison.OrdinalIgnoreCase);
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
