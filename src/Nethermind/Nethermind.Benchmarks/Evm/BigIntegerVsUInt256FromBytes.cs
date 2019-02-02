/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Benchmarks.Evm
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class BigIntegerVsUInt256FromBytes
    {
        private static Random _random = new Random(0);

        private byte[] _stack;

        [Params(true, false)] public bool AllZeros { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _stack = new byte[64];
            if (!AllZeros)
            {
                _random.NextBytes(_stack);
            }
        }

        [Benchmark]
        public (BigInteger, BigInteger) BigInteger()
        {
            Span<byte> span = _stack.AsSpan();
            BigInteger a = new BigInteger(span.Slice(0, 32), true, true);
            BigInteger b = new BigInteger(span.Slice(32, 32), true, true);
            return (a, b);
        }

        [Benchmark]
        public (UInt256, UInt256) UInt256()
        {
            Span<byte> span = _stack.AsSpan();
            Dirichlet.Numerics.UInt256.CreateFromBigEndian2(out UInt256 a, span.Slice(0, 32));
            Dirichlet.Numerics.UInt256.CreateFromBigEndian2(out UInt256 b, span.Slice(32, 32));
            return (a, b);
        }
    }
}