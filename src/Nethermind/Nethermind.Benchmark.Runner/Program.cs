//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Linq;

namespace Nethermind.Benchmark.Runner
{
    public class DashboardConfig : ManualConfig
    {
        public DashboardConfig(params Job[] jobs) 
        {
            foreach (Job job in jobs)
            {
                AddJob(job);
            }
            
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Statistics);
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Params);
            AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
            AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
            WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(100));
        }
    }
    
    public static class Program
    {
        public static void Main(string[] args)
        {
            List<Assembly> additionalJobAssemblies = new(){
                typeof(Nethermind.JsonRpc.Benchmark.EthModuleBenchmarks).Assembly,
                typeof(Nethermind.Benchmarks.Core.Keccak256Benchmarks).Assembly,
                typeof(Nethermind.Evm.Benchmark.EvmStackBenchmarks).Assembly,
                typeof(Nethermind.Network.Benchmarks.DiscoveryBenchmarks).Assembly,
                typeof(Nethermind.Precompiles.Benchmark.KeccakBenchmark).Assembly
            };

            List<Assembly> simpleJobAssemblies = new() {
                typeof(Nethermind.EthereumTests.Benchmark.EthereumTests).Assembly,
            };
            
            if (Debugger.IsAttached)
            {
                BenchmarkSwitcher.FromAssemblies(additionalJobAssemblies.Union(simpleJobAssemblies).ToArray()).RunAll(new DebugInProcessConfig());
            }
            else
            {
                foreach (Assembly assembly in additionalJobAssemblies)
                {
                    BenchmarkRunner.Run(assembly, new DashboardConfig(Job.MediumRun.WithRuntime(CoreRuntime.Core50)));    
                }    
                
                foreach (Assembly assembly in simpleJobAssemblies)
                {
                    BenchmarkRunner.Run(assembly, new DashboardConfig());    
                }  
            }
        }
    }
}
