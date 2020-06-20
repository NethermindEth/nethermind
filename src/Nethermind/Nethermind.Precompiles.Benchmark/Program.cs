﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Nethermind.Precompiles.Benchmark
{
    public class Program
    {
        public static void Main(string[] args)
#if DEBUG
=> BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new DebugInProcessConfig());
#else
        {
            // BenchmarkRunner.Run<Sha256Benchmark>();
            // BenchmarkRunner.Run<RipEmdBenchmark>();
            // BenchmarkRunner.Run<Blake2fBenchmark>();
            BenchmarkRunner.Run<KeccakBenchmark>();
            // BenchmarkRunner.Run<BnPairBenchmark>();
            // BenchmarkRunner.Run<BnAddBenchmark>();
            // BenchmarkRunner.Run<BnMulBenchmark>();
        }
#endif
    }
}