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
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Nethermind.Benchmarks.Core;
using Nethermind.Core.Caching;

namespace Nethermind.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            IConfig config = Debugger.IsAttached ? new DebugInProcessConfig() : null;
            BenchmarkRunner.Run<LruCacheAddAtCapacityBenchmarks>(config);
            // BenchmarkRunner.Run<LruCacheBenchmarks>(config);
            // BenchmarkRunner.Run<LruCacheKeccakBytesBenchmarks>(config);
            // BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        }
    }
}