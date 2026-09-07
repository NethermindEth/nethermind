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
using Nethermind.Benchmarks.Store;

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
            if (args.Contains("--rocksdb-feature-standalone"))
            {
                RunRocksDbFeatureStandalone(args);
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

        private static void RunRocksDbFeatureStandalone(string[] args)
        {
            RocksDbFeatureDatasetKind dataset = Enum.Parse<RocksDbFeatureDatasetKind>(
                GetArgument(args, "--rocksdb-feature-dataset", nameof(RocksDbFeatureDatasetKind.Account)), true);
            RocksDbFeatureVariant variant = Enum.Parse<RocksDbFeatureVariant>(
                GetArgument(args, "--rocksdb-feature-variant", nameof(RocksDbFeatureVariant.Baseline)), true);
            int operations = int.Parse(GetArgument(args, "--rocksdb-feature-operations", "1000"));

            RocksDbFeatureStandaloneRunner.Run(dataset, variant, operations);
        }

        private static string GetArgument(string[] args, string name, string defaultValue)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }

            return defaultValue;
        }
    }
}
