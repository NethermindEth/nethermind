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

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;

namespace Nethermind.Perfshop
{
    [MemoryDiagnoser]
    [DisassemblyDiagnoser(printAsm: true)]
    [CoreJob(baseline: true)]
    public class SumBenchmark
    {
        private byte[] Scenario0 = new byte[1024];

        [GlobalSetup]
        public void Setup()
        {
            new Random().NextBytes(Scenario0);
        }

        [Benchmark]
        public int Foreach2()
        {
            int result = 0;
            foreach (int b in Scenario0)
            {
                result += b;
            }

            return result;
        }
        
        [Benchmark(Baseline = true)]
        public int Foreach()
        {
            int result = 0;
            foreach (byte b in Scenario0)
            {
                result += b;
            }

            return result;
        }

        [Benchmark]
        public int For()
        {
            int result = 0;
            for (int i = 0; i < Scenario0.Length; i++)
            {
                result += Scenario0[i];
            }

            return result;
        }
        
        [Benchmark]
        public int For2()
        {
            int result = 0;
            int length = Scenario0.Length;
            for (int i = 0; i < length; i++)
            {
                result += Scenario0[i];
            }

            return result;
        }
        
        [Benchmark]
        public int Sum()
        {
            return Scenario0.Sum(b => (int) b);
        }
    }
}