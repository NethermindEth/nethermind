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
            IlAnalyzer.Initialize();

            byte[] bytes = new byte[32];
            ((UInt256)4999).ToBigEndian(bytes);
            var argBytes = bytes.WithoutLeadingZeros().ToArray();
            byte[] bytecode = Prepare.EvmCode
                        .PushData(argBytes)
                        .COMMENT("1st/2nd fib number")
                        .PushData(0)
                        .PushData(1)
                        .COMMENT("MAINLOOP:")
                        .JUMPDEST()
                        .DUPx(3)
                        .ISZERO()
                        .PushData(26 + argBytes.Length)
                        .JUMPI()
                        .COMMENT("fib step")
                        .DUPx(2)
                        .DUPx(2)
                        .ADD()
                        .SWAPx(2)
                        .POP()
                        .SWAPx(1)
                        .COMMENT("decrement fib step counter")
                        .SWAPx(2)
                        .PushData(1)
                        .SWAPx(1)
                        .SUB()
                        .SWAPx(2)
                        .PushData(5 + argBytes.Length).COMMENT("goto MAINLOOP")
                        .JUMP()

                        .COMMENT("CLEANUP:")
                        .JUMPDEST()
                        .SWAPx(2)
                        .POP()
                        .POP()
                        .COMMENT("done: requested fib number is the only element on the stack!")
                        .STOP()
                        .Done;

            var nrml = new LocalSetup<NotTracing, NotOptimizing>("ILEVM::std", bytecode, null);
            var ilvm = new LocalSetup<NotTracing, IsOptimizing>("ILEVM::aot", bytecode, ILMode.FULL_AOT_MODE);

            Run(ilvm, 1_000);
            Run(nrml, 1_000);

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

        public static void _Main(string[] args)
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
                default:
                    throw new Exception("Invalid mode");
            }
        }

        public static void RunEvmBenchmarks(Options options)
        {
            if (String.IsNullOrEmpty(options.ByteCode) || String.IsNullOrEmpty(options.Name))
            {
                BenchmarkRunner.Run(typeof(Nethermind.Evm.Benchmark.EvmBenchmarks), new DashboardConfig(Job.LongRun.WithRuntime(CoreRuntime.Core90)));
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
