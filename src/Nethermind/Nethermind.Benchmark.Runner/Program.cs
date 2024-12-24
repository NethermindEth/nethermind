// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


// #define BENCHMARK
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Linq;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Evm.Benchmark;
using Nethermind.Evm.Config;
using Nethermind.Abi;
using Nethermind.Evm;
using System;
using Nethermind.Core.Extensions;
using NSubstitute;
using Nethermind.Int256;
using BenchmarkDotNet.Toolchains.DotNetCli;
using CommandLine;
using System.IO;

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

            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Descriptor);
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Statistics);
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Params);
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Metrics);
            AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
            AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
            WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(100));
        }
    }

    public static class Program
    {
        public class Options
        {
            [Option('m', "mode", Default = "full", Required = true, HelpText = "Available modes: full, evm")]
            public string Mode { get; set; }

            [Option('b', "bytecode", Required = false, HelpText = "Hex encoded bytecode")]
            public string ByteCode { get; set; }

            [Option('n', "identifier", Required = false, HelpText = "Benchmark Name")]
            public string Name { get; set; }

        }


        public static void Main(string[] args)
        {
            ParserResult<Options> options = Parser.Default.ParseArguments<Options>(args);
            switch (options.Value.Mode)
            {
                case "full":
                    RunFullBenchmark(args);
                    break;
                case "evm":
                    RunEvmBenchmarks(options.Value);
                    break;
                default:
                    throw new Exception("Invalid mode");
            }
        }

        public static void RunEvmBenchmarks(Options options)
        {
            if (String.IsNullOrEmpty(options.ByteCode) || String.IsNullOrEmpty(options.Name))
            {
                BenchmarkRunner.Run(typeof(Nethermind.Evm.Benchmark.EvmBenchmarks), new DashboardConfig(Job.MediumRun.WithRuntime(CoreRuntime.Core90)));
            }
            else
            {
                string bytecode = options.ByteCode;
                if (Path.Exists(bytecode))
                {
                    bytecode = File.ReadAllText(bytecode);
                }

                Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.CODE", bytecode);
                Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.Name", options.Name);
                var summary = BenchmarkRunner.Run<CustomEvmBenchmarks>(new DashboardConfig(Job.MediumRun.WithRuntime(CoreRuntime.Core90)));
            }
        }

        public static void RunFullBenchmark(string[] args)
        {
            List<Assembly> additionalJobAssemblies = new()
            {
                typeof(JsonRpc.Benchmark.EthModuleBenchmarks).Assembly,
                typeof(Benchmarks.Core.Keccak256Benchmarks).Assembly,
                typeof(Evm.Benchmark.EvmStackBenchmarks).Assembly,
                typeof(Network.Benchmarks.DiscoveryBenchmarks).Assembly,
                typeof(Precompiles.Benchmark.KeccakBenchmark).Assembly
            };

            List<Assembly> simpleJobAssemblies = new()
            {
                typeof(EthereumTests.Benchmark.EthereumTests).Assembly,
            };

            if (Debugger.IsAttached)
            {
                BenchmarkSwitcher.FromAssemblies(additionalJobAssemblies.Union(simpleJobAssemblies).ToArray()).RunAll(new DebugInProcessConfig());
            }
            else
            {
                foreach (Assembly assembly in additionalJobAssemblies)
                {
                    BenchmarkRunner.Run(assembly, new DashboardConfig(Job.MediumRun.WithRuntime(CoreRuntime.Core80)), args);
                }

                foreach (Assembly assembly in simpleJobAssemblies)
                {
                    BenchmarkRunner.Run(assembly, new DashboardConfig(), args);
                }
            }
        }
    }
}
