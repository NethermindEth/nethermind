// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
using BenchmarkDotNet.ConsoleArguments;
using BenchmarkDotNet.ConsoleArguments.ListBenchmarks;
using BenchmarkDotNet.Loggers;
using Nethermind.Merge.Plugin.Benchmark;
using Nethermind.Precompiles.Benchmark;
using TypeFilter = BenchmarkDotNet.Running.TypeFilter;

namespace Nethermind.Benchmark.Runner
{
    public class DashboardConfig : ManualConfig
    {
        public DashboardConfig()
        {
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

        public DashboardConfig(Job job) : this() => AddJob(job.AsDefault());
    }

    public class PrecompileBenchmarkConfig : DashboardConfig
    {
        public PrecompileBenchmarkConfig() =>
            AddColumnProvider(new GasColumnProvider());

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
            Job benchmarkJob = (quickMode ? Job.ShortRun : Job.Default).WithRuntime(CoreRuntime.Core10_0)
                .WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings("net11.0", null, ".NET 11.0")));

            List<Assembly> additionalJobAssemblies = [
                typeof(JsonRpc.Benchmark.EthModuleBenchmarks).Assembly,
                typeof(Benchmarks.Core.Keccak256Benchmarks).Assembly,
                typeof(Evm.Benchmark.EvmStackBenchmarks).Assembly,
                typeof(Network.Benchmarks.DiscoveryBenchmarks).Assembly,
                typeof(NewPayloadSerializationBenchmarks).Assembly,
                typeof(Abi.Benchmark.F).Assembly,
                typeof(Serialization.FluentRlp.Benchmark.NestedCollectionsBenchmark).Assembly,
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

                Run(releaseAssemblies, benchmarkArgs, benchmarkJob);
            }
        }

        private static void Run(Assembly[] assemblies, string[] args, Job runnerJob)
        {
            ManualConfig ownJobConfig = new PrecompileBenchmarkConfig();
            ILogger logger = ConsoleLogger.Default;
            (bool isSuccess, IConfig parsedConfig, CommandLineOptions options) = ConfigParser.Parse(args, logger, ownJobConfig);
            if (!isSuccess)
            {
                return;
            }

            (bool allTypesValid, IReadOnlyList<Type> types) = TypeFilter.GetTypesWithRunnableBenchmarks([], assemblies, logger);
            if (!allTypesValid)
            {
                return;
            }

            if (options.PrintInformation || options.ListBenchmarkCaseMode != ListBenchmarkCaseMode.Disabled)
            {
                BenchmarkSwitcher.FromTypes([.. types]).Run(args, ownJobConfig);
                return;
            }

            ILookup<bool, Type> typesByOwnJob = types.ToLookup(DeclaresJob);
            ManualConfig runnerJobConfig = new PrecompileBenchmarkConfig(runnerJob);
            string[] groupArgs = args;
            if (options.Resume)
            {
                string artifactsPath = parsedConfig.ArtifactsPath ?? DefaultConfig.Instance.ArtifactsPath;
                runnerJobConfig.WithArtifactsPath(Path.Combine(artifactsPath, "runner-job"));
                ownJobConfig.WithArtifactsPath(Path.Combine(artifactsPath, "own-job"));
                groupArgs = WithoutArtifactsPath(args);
            }

            GroupRun runnerJobRun = RunMatching([.. typesByOwnJob[false]], groupArgs, runnerJobConfig, parsedConfig);
            if (runnerJobRun == GroupRun.Failed && options.StopOnFirstError)
            {
                return;
            }

            GroupRun ownJobRun = RunMatching([.. typesByOwnJob[true]], groupArgs, ownJobConfig, parsedConfig);
            if (runnerJobRun == GroupRun.NoMatch && ownJobRun == GroupRun.NoMatch)
            {
                BenchmarkSwitcher.FromTypes([.. types]).Run(args, ownJobConfig);
            }
        }

        private enum GroupRun
        {
            NoMatch,
            Succeeded,
            Failed,
        }

        private static GroupRun RunMatching(Type[] types, string[] args, IConfig config, IConfig parsedConfig)
        {
            if (TypeFilter.Filter(ManualConfig.Union(config, parsedConfig), types).Length == 0)
            {
                return GroupRun.NoMatch;
            }

            IEnumerable<Summary> summaries = BenchmarkSwitcher.FromTypes(types).Run(args, config);
            return summaries.Any(static summary => summary.HasCriticalValidationErrors || summary.Reports.Any(static report => !report.Success))
                ? GroupRun.Failed
                : GroupRun.Succeeded;
        }

        private static string[] WithoutArtifactsPath(string[] args)
        {
            List<string> result = [with(args.Length)];
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "-a" or "--artifacts")
                {
                    i++;
                }
                else if (!args[i].StartsWith("--artifacts=", StringComparison.Ordinal))
                {
                    result.Add(args[i]);
                }
            }

            return [.. result];
        }

        private static bool DeclaresJob(Type type) =>
            type.GetCustomAttributes(true).OfType<IConfigSource>()
                .Concat(type.Assembly.GetCustomAttributes().OfType<IConfigSource>())
                .Any(static source => source.Config.GetJobs().Any(static job => !job.Meta.IsMutator));
    }
}
