// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Linq;
using System;
using CommandLine;

namespace Nethermind.Benchmark.Runner
{
    public static class Program
    {
        public class Options
        {
            [Option('m', "mode", Default = "full", Required = false, HelpText = "Available modes: full, precompiles, precompilesBytecode")]
            public string Mode { get; set; }

            [Option('f', "filter", Required = false, HelpText = "Use to filter tests by name(s)")]
            public IEnumerable<string>  Filter { get; set; }
        }


        static void Main(string[] args)
        {
            IConfig config = new DashboardConfig(Enumerable.Empty<string>(), Job.MediumRun.WithRuntime(CoreRuntime.Core60));
            List<Assembly> assemblies = new List<Assembly>();

            Parser.Default.ParseArguments<Options>(args)
                   .WithParsed<Options>(o =>
                   {
                        if (o.Mode == "full") 
                        {
                            ExecuteFullBenchmark(o.Filter);
                        } 
                        else if (o.Mode == "precompiles")
                        {
                            ExecutePrecompilesBenchmark(o.Filter);
                        }
                        else if (o.Mode == "precompilesBytecode")
                        {
                            ExecutePrecompilesBytecodeBenchmark();
                        } 
                        else 
                        {
                            throw new Exception("Unknown mode");
                        }
                   });
        }

        static void ExecuteFullBenchmark(IEnumerable<string> filters) 
        {
            var config = new DashboardConfig(filters, Job.MediumRun.WithRuntime(CoreRuntime.Core60));

            Assembly[] assemblies = 
            {
                typeof(Nethermind.JsonRpc.Benchmark.EthModuleBenchmarks).Assembly,
                typeof(Nethermind.Benchmarks.Core.Keccak256Benchmarks).Assembly,
                typeof(Nethermind.Evm.Benchmark.EvmStackBenchmarks).Assembly,
                typeof(Nethermind.Network.Benchmarks.DiscoveryBenchmarks).Assembly,
                typeof(Nethermind.Precompiles.Benchmark.KeccakBenchmark).Assembly,
                typeof(Nethermind.EthereumTests.Benchmark.EthereumTests).Assembly,
            };
            
            if (Debugger.IsAttached)
            {
                BenchmarkSwitcher.FromAssemblies(assemblies).RunAll(new DebugInProcessConfig());
            }
            else
            {
                foreach (Assembly assembly in assemblies)
                {
                    BenchmarkRunner.Run(assembly, config);
                }
            }
        }

        private static void ExecutePrecompilesBytecodeBenchmark()
        {
            throw new NotImplementedException();
        }

        private static void ExecutePrecompilesBenchmark(IEnumerable<string> filters)
        {
            var benchmarkTypes = new []
            {
                typeof(Nethermind.Precompiles.Benchmark.Blake2fBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.Bn256AddBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.Bn256MulBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.Bn256PairingBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.EcRecoverBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.KeccakBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.ModExpBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.PointEvaluationBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.RipEmdBenchmark),
                typeof(Nethermind.Precompiles.Benchmark.Sha256Benchmark),
            };

            var config = new NoOutputConfig(filters, Job.ShortRun.WithRuntime(CoreRuntime.Core60));

            var firstLine = true;
            foreach (var bType in benchmarkTypes)
            {
                var summary = BenchmarkRunner.Run(bType, config);
                
                foreach(var report in summary.Reports)
                {
                    if (firstLine)
                    {
                        Console.WriteLine("Benchmark Process Environment Information:");
                        Console.WriteLine("Runtime=" + report.GetRuntimeInfo());
                        Console.WriteLine("GC=" + report.GetGcInfo());
                        Console.WriteLine();
                        Console.WriteLine($"Benchmark,Test,NominalGasCost,RunsCount,TimeNs,MemGcOps,MemAllocPerOp");
                        firstLine = false;
                    }

                    var parameterJson = Newtonsoft.Json.JsonConvert.SerializeObject(report.BenchmarkCase.Parameters.Items[0].Value);
                    dynamic parameter = Newtonsoft.Json.JsonConvert.DeserializeObject(parameterJson);

                    long gasCost = parameter.GasCost;
                    string testName = parameter.Name;

                    // if gas is missing try to extract from the file name
                    if (gasCost == 0 && testName.EndsWith(".csv"))
                    {
                        var gasCostString = testName.Substring(0, testName.Length - 4).Split('_').Last();
                        long.TryParse(gasCostString, out gasCost);
                    }

                    var memAllocPerOp = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase);

                    Console.WriteLine($"{bType.Name},{parameter.Name},{gasCost},{report.ResultStatistics.N},{report.ResultStatistics.Mean},{report.GcStats.TotalOperations},{memAllocPerOp}");
                }
            }
        }
    }
}
