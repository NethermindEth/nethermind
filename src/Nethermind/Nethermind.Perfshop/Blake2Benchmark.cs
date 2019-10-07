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

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Perfshop
{
    [MemoryDiagnoser]
    [DisassemblyDiagnoser(printAsm: true)]
    [CoreJob(baseline: true)]
    public class Blake2Benchmark
    {
        private Blake2 _blake2 = new Blake2();
        private Blake2Optimized _blake2Optimized = new Blake2Optimized();

        private byte[] input = Bytes.FromHexString("0000000148c9bdf267e6096a3ba7ca8485ae67bb2bf894fe72f36e3cf1361d5f3af54fa5d182e6ad7f520e511f6c3e2b8c68059b6bbd41fbabd9831f79217e1319cde05b61626300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000300000000000000000000000000000001");

        [GlobalSetup]
        public void Setup()
        {
            if (!Bytes.AreEqual(Blake2FirstVersion(), Blake2OptimizedVersion()))
            {
                throw new InvalidBenchmarkDeclarationException("blakes");
            }
        }

        [Benchmark(Baseline = true)]
        public byte[] Blake2FirstVersion()
        {
            return _blake2.Compress(input);
        }

        [Benchmark]
        public byte[] Blake2OptimizedVersion()
        {
            return _blake2Optimized.Compress(input);
        }
    }
}