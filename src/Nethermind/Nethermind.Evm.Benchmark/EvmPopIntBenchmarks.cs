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
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class EvmPopIntBenchmarks
    {
        // public property
        public IEnumerable<UInt256> ValueSource => new[] { UInt256.Parse("125124123718263172357123"), UInt256.Parse("0"), UInt256.MaxValue};
        
        private byte[] stackBytes;
        private ITxTracer _tracer = NullTxTracer.Instance;

        [GlobalSetup]
        public void GlobalSetup()
        {
            stackBytes = new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * 1024];
            EvmStack stack = new EvmStack(stackBytes, 0, _tracer);
            foreach (UInt256 bigInteger in ValueSource)
            {
                stack.PushUInt256(in bigInteger);   
            }
        }

        [Benchmark(Baseline = true)]
        public (UInt256, UInt256, UInt256)  Current()
        {
            EvmStack stack = new EvmStack(stackBytes.AsSpan(), 3, _tracer);
            stack.PopUInt256(out UInt256 result1);
            stack.PopUInt256(out UInt256 result2);
            stack.PopUInt256(out UInt256 result3);

            return (result1, result2, result3);
        }
    }
}