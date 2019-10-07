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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;

namespace Nethermind.Perfshop
{
    [MemoryDiagnoser]
    [DisassemblyDiagnoser(printAsm: true)]
    [CoreJob(baseline: true)]
    public class ReverseBytesBenchmark
    {
        private byte[] Scenario0 = Bytes.FromHexString("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

        [GlobalSetup]
        public void Setup()
        {
            byte[] clone = Scenario0.Clone() as byte[];
            
            ArrayVersion();
            ArrayVersion();
            if (!Bytes.AreEqual(clone, Scenario0))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(ArrayVersion)}");
            }
            
            Avx2Version();
            Avx2Version();
            if (!Bytes.AreEqual(clone, Scenario0))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(Avx2Version)}");
            }
            
            SpanVersion();
            SpanVersion();
            if (!Bytes.AreEqual(clone, Scenario0))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(SpanVersion)}");
            }
            
            NaiveVersion();
            NaiveVersion();
            if (!Bytes.AreEqual(clone, Scenario0))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(NaiveVersion)}");
            }
            
            NaiveInPlaceVersion();
            NaiveInPlaceVersion();
            if (!Bytes.AreEqual(clone, Scenario0))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(NaiveInPlaceVersion)}");
            }
        }

        [Benchmark(Baseline = true)]
        public void ArrayVersion()
        {
            Array.Reverse(Scenario0);
        }

        [Benchmark]
        public void Avx2Version()
        {
            Bytes.Avx2Reverse256InPlace(Scenario0);
        }
        
        [Benchmark]
        public void SpanVersion()
        {
            Scenario0.AsSpan().Reverse();
        }

        [Benchmark]
        public void NaiveInPlaceVersion()
        {
            Bytes.ReverseInPlace(Scenario0);
        }
        
        [Benchmark]
        public void NaiveVersion()
        {
            Bytes.Reverse(Scenario0);
        }
    }
}