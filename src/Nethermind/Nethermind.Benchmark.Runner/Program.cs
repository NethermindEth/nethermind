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

using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Nethermind.Benchmark.Runner
{
    public class DashboardConfig : ManualConfig
    {
        public DashboardConfig()
        {
            AddJob(Job.MediumRun.WithRuntime(CoreRuntime.Core50));
            AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Statistics);
            AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
            AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
        }
    }
    
    public static class Program
    {
        public static void Main(string[] args)
        {
            IConfig config = Debugger.IsAttached ? new DebugInProcessConfig() : new DashboardConfig();
            
            Assembly[] assemblies =
            {
                typeof(Nethermind.JsonRpc.Benchmark.EthModuleBenchmarks).Assembly,
                typeof(Nethermind.Benchmarks.Core.Keccak256Benchmarks).Assembly,
                typeof(Nethermind.Evm.Benchmark.EvmStackBenchmarks).Assembly,
                typeof(Nethermind.Network.Benchmarks.DiscoveryBenchmarks).Assembly,
                typeof(Nethermind.Precompiles.Benchmark.KeccakBenchmark).Assembly
            };

            if (Debugger.IsAttached)
            {
                BenchmarkSwitcher.FromAssemblies(assemblies).RunAll(config);
            }
            else
            {
                foreach (Assembly assembly in assemblies)
                {
                    BenchmarkRunner.Run(assembly, config);    
                }    
            }
        }
    }
}
