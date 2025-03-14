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
using Nethermind.Evm.CodeAnalysis.IL;
using static Nethermind.Evm.VirtualMachine;
using Microsoft.Diagnostics.Runtime;
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
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
            WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(100));
        }
    }

    public class PrecompileBenchmarkConfig : DashboardConfig
    {
        public PrecompileBenchmarkConfig() : base(Job.MediumRun.WithRuntime(CoreRuntime.Core90))
        {
            AddColumnProvider(new GasColumnProvider());
        }
    }

    public static class Program
    {
        public class Options
        {
            [Option('m', "mode", Default = "full", Required = true, HelpText = "Available modes: full, evm, ilevm")]
            public string Mode { get; set; }

            [Option('b', "bytecode", Required = false, HelpText = "Hex encoded bytecode")]
            public string ByteCode { get; set; }

            [Option('n', "identifier", Required = false, HelpText = "Benchmark Name")]
            public string Name { get; set; }

            [Option('c', "config", Required = false, HelpText = "EVM configs : 0-STD, 1-PAT, 2-AOT")]
            public string Config { get; set; }
        }

        public static void Run(ILocalSetup setup, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                setup.Setup();
                setup.Run();
                setup.Reset();
            }
        }

        public static void Main(string[] args)
        {
            IlAnalyzer.Initialize();

            ParserResult<Options> options = Parser.Default.ParseArguments<Options>(args);
            switch (options.Value.Mode)
            {
                case "full":
                    RunFullBenchmark(args);
                    break;
                case "evm":
                    RunEvmBenchmarks(options.Value);
                    break;
                case "ilevm":
                    RunIlEvmBenchmarks(options.Value);
                    break;
                default:
                    throw new Exception("Invalid mode");
            }
        }

        public static void RunEvmBenchmarks(Options options)
        {
            int mode = 1 | 2 | 4 | 8;
            Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.MODE", mode.ToString());

            if (String.IsNullOrEmpty(options.ByteCode) || String.IsNullOrEmpty(options.Name))
            {
                BenchmarkRunner.Run(typeof(Nethermind.Evm.Benchmark.EvmBenchmarks), new DashboardConfig(Job.VeryLongRun.WithRuntime(CoreRuntime.Core90)));
            }
            else
            {
                string bytecode = options.ByteCode;
                if (Path.Exists(bytecode))
                {
                    bytecode = File.ReadAllText(bytecode);
                }

                Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.CODE", bytecode);
                Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.NAME", options.Name);
                var summary = BenchmarkRunner.Run<CustomEvmBenchmarks>(new DashboardConfig(Job.MediumRun.WithRuntime(CoreRuntime.Core90)));
            }
        }


        public static void RunIlEvmBenchmarks(Options options)
        {
            Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.MODE", options.Config);

            if (String.IsNullOrEmpty(options.ByteCode) || String.IsNullOrEmpty(options.Name))
            {
                BenchmarkRunner.Run(typeof(Nethermind.Evm.Benchmark.EvmBenchmarks), new DashboardConfig(Job.VeryLongRun.WithRuntime(CoreRuntime.Core90)));
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
                    BenchmarkRunner.Run(assembly, new DashboardConfig(Job.MediumRun.WithRuntime(CoreRuntime.Core80)), args);
                }

                foreach (Assembly assembly in simpleJobAssemblies)
                {
                    BenchmarkRunner.Run(assembly, new DashboardConfig(), args);
                }

                BenchmarkRunner.Run(typeof(KeccakBenchmark).Assembly, new PrecompileBenchmarkConfig(), args);
            }
        }
    }
}
