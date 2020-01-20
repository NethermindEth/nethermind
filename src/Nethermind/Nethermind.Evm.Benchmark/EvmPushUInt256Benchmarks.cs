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
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class EvmPushUInt256Benchmarks
    {
        [ParamsSource(nameof(ValueSource))]
        public UInt256 value;

        // public property
        public IEnumerable<UInt256> ValueSource => new[] { UInt256.One, UInt256.MaxValue};
        
        private byte[] stackBytes;
        private ITxTracer _tracer = NullTxTracer.Instance;

        [GlobalSetup]
        public void GlobalSetup()
        {
            stackBytes = new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * 1024];
        }

        [Benchmark(Baseline = true, OperationsPerInvoke = 4)]
        public void Current()
        {
            EvmStack stack = new EvmStack(stackBytes.AsSpan(), 0, _tracer);
            
            stack.PushUInt256(ref value);
            stack.PopLimbo();
            
            stack.PushUInt256(ref value);
            stack.PopLimbo();
            
            stack.PushUInt256(ref value);
            stack.PopLimbo();
            
            stack.PushUInt256(ref value);
            stack.PopLimbo();
        }
    }
}