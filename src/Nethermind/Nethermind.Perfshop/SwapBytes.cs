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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Perfshop
{
    [MemoryDiagnoser]
    [DisassemblyDiagnoser(printAsm:true)]
    [CoreJob(baseline: true)]
    public class SwapBytes
    {
        private const ulong number = 1230123812841984UL;
        
        [Benchmark(Baseline = true)]
        public void Custom()
        {
            UInt256.SwapBytes(number);
        }
        
        [Benchmark]
        public void ReverseEndianness()
        {
            UInt256.SwapBytes2(number);
        }
        
        [Benchmark]
        public void HostToNetwork()
        {
            UInt256.SwapBytes3(number);
        }
    }
}