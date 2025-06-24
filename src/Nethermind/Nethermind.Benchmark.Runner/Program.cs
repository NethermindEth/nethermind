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
using System.Threading.Tasks;
using System.Threading;
using Perfolizer.Horology;
using BenchmarkDotNet.Toolchains.CsProj;

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
            WithBuildTimeout(TimeSpan.MaxValue);
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
            [Option('m', "mode", Default = "full", Required = true, HelpText = "Available modes: full, evm, ilevm, ilevm-weth, evm-weth")]
            public string Mode { get; set; }

            [Option('b', "bytecode", Required = false, HelpText = "Hex encoded bytecode")]
            public string ByteCode { get; set; }

            [Option('n', "identifier", Required = false, HelpText = "Benchmark Name")]
            public string Name { get; set; }

            [Option('c', "config", Required = false, HelpText = "EVM configs : 1-STD, 2-AOT")]
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
            ParserResult<Options> options = Parser.Default.ParseArguments<Options>(args);
            switch (options.Value.Mode)
            {
                case "full":
                    RunFullBenchmark(args);
                    break;
                case "evm":
                case "ilevm":
                    RunEvmBenchmarks(options.Value);
                    break;
                case "evm-ilevm":
                    RunIlEvmSuite(options.Value);
                    break;
                case "weth-bench":
                    // spawn a new process to run the WETH benchmarks
                    RunWethBenchmarksInIsolation(options.Value);
                    break;
                default:
                    throw new Exception("Invalid mode");
            }
        }

        private static void RunIlEvmSuite(Options value)
        {
            var summary = BenchmarkRunner.Run([
                            typeof(Nethermind.Evm.Benchmark.Fib),
                            typeof(Nethermind.Evm.Benchmark.Prime),
                            typeof(Nethermind.Evm.Benchmark.Weth)
                        ], new DashboardConfig(Job.VeryLongRun
                                .WithPlatform(Platform.X64)
                                .WithJit(Jit.RyuJit)
                                .WithRuntime(CoreRuntime.Core90)
                                ));
        }

        private static void RunWethBenchmarksInIsolation(Options value)
        {
            ILMode mode = (ILMode)Int32.Parse(value.Config ?? string.Empty);

            var config = new DashboardConfig(Job.VeryLongRun.WithToolchain(BenchmarkDotNet.Toolchains.CsProj.CsProjCoreToolchain.NetCoreApp90));

            if (mode == (ILMode.NO_ILVM | ILMode.AOT_MODE))
            {
                BenchmarkRunner.Run(typeof(Nethermind.Evm.Benchmark.WrapedEthBenchmarks), config);
            }
            else if (mode == ILMode.AOT_MODE)
            {
                BenchmarkRunner.Run<WrapedEthBenchmarksSetup<VirtualMachine.IsPrecompiling>>(config);
            }
            else if (mode == ILMode.NO_ILVM)
            {
                BenchmarkRunner.Run<WrapedEthBenchmarksSetup<VirtualMachine.NotOptimizing>>(config);
            }
        }

        public static void RunEvmBenchmarks(Options options)
        {
            Environment.SetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.MODE", options.Config);

            var config = new DashboardConfig(Job.VeryLongRun.WithToolchain(BenchmarkDotNet.Toolchains.CsProj.CsProjCoreToolchain.NetCoreApp90));

            if (String.IsNullOrEmpty(options.ByteCode) || String.IsNullOrEmpty(options.Name))
            {
                BenchmarkRunner.Run(typeof(Nethermind.Evm.Benchmark.EvmBenchmarks), config);
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
                var summary = BenchmarkRunner.Run<CustomEvmBenchmarks>(config);
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
