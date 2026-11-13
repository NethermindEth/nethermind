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
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
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
            bool quickMode = args.Contains("--quick");
            string[] benchmarkArgs = args.Where(static arg => arg != "--quick").ToArray();
            // BenchmarkDotNet 0.15.8 doesn't recognize .NET 11 and fails validating such a job, so it claims .NET 10
            // and the toolchain builds for net11.0. TODO: use the host runtime once BenchmarkDotNet 0.16.0 is released.
            Job benchmarkJob = (quickMode ? Job.ShortRun : Job.MediumRun).WithRuntime(CoreRuntime.Core10_0)
                .WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings("net11.0", null, ".NET 11.0")));

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
