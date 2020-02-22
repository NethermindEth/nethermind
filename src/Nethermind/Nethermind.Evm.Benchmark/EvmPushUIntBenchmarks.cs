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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class EvmPushUIntBenchmarks
    {
        [ParamsSource(nameof(ValueSource))]
        public BigInteger value;

        // public property
        public IEnumerable<BigInteger> ValueSource => new[] { BigInteger.Parse("125124123718263172357123"), BigInteger.Parse("0"), UInt256.MaxValue};
        
        private byte[] stackBytes;
        private ITxTracer _tracer = NullTxTracer.Instance;

        [GlobalSetup]
        public void GlobalSetup()
        {
            stackBytes = new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * 1024];
        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            EvmStack stack = new EvmStack(stackBytes.AsSpan(), 0, _tracer);
            stack.PushUInt(ref value);
            stack.PopLimbo();
        }
    }
}