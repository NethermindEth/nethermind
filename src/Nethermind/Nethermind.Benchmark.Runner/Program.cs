// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

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
            if (args.Contains("--rocksdb-account-auto-standalone"))
            {
                RunRocksDbAccountAutoStandalone(args);
                return;
            }

            if (args.Contains("--rocksdb-account-auto-benchmarks"))
            {
                RocksDbAccountAutoBenchmarkSelection.Configure(
                    GetOptionalEnum<RocksDbAccountAutoMode>(args, "--rocksdb-account-auto-mode"));
            }

            bool quickMode = args.Contains("--quick");
            string[] benchmarkArgs = RemoveAccountAutoArguments(args);
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

        private static void RunRocksDbAccountAutoStandalone(string[] args)
        {
            RocksDbAccountAutoMode mode = Enum.Parse<RocksDbAccountAutoMode>(
                GetArgument(args, "--rocksdb-account-auto-mode", nameof(RocksDbAccountAutoMode.BinaryBaseline)), true);
            int operations = int.Parse(GetArgument(args, "--rocksdb-account-auto-operations", "4096"));

            RocksDbAccountAutoStandaloneRunner.Run(mode, operations);
        }

        private static TEnum? GetOptionalEnum<TEnum>(string[] args, string name) where TEnum : struct, Enum
        {
            string? value = GetOptionalArgument(args, name);
            if (value is null) return null;
            if (Enum.TryParse(value, ignoreCase: true, out TEnum result)) return result;
            throw new ArgumentException($"Unknown {name} value '{value}'.", name);
        }

        private static string? GetOptionalArgument(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }

            return null;
        }

        private static string GetArgument(string[] args, string name, string defaultValue) =>
            GetOptionalArgument(args, name) ?? defaultValue;

        private static string[] RemoveAccountAutoArguments(string[] args)
        {
            List<string> benchmarkArgs = [];
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "--quick" or "--rocksdb-account-auto-benchmarks") continue;
                if (args[i] is "--rocksdb-account-auto-mode" or "--rocksdb-account-auto-operations")
                {
                    i++;
                    continue;
                }

                benchmarkArgs.Add(args[i]);
            }

            return benchmarkArgs.ToArray();
        }
    }
}
