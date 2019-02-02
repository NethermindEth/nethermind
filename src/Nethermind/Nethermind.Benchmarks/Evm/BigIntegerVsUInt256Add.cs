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
    public class BigIntegerVsUInt256Add
    {
        private static Random _random = new Random(0);

        private byte[] _stack;

        [Params(true, false)]
        public bool AllZeros { get; set; }

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
        public void BigInteger()
        {
            Span<byte> span = _stack.AsSpan();
            BigInteger a = new BigInteger(span.Slice(0, 32), true, true);
            BigInteger b = new BigInteger(span.Slice(32, 32), true, true);

            BigInteger value = a + b;
            Span<byte> target = span.Slice(0, 32);
            int bytesToWrite = value.GetByteCount(true);
            if (bytesToWrite > 32)
                bytesToWrite = 32;
            if (bytesToWrite != 32)
            {
                target.Clear();
                target = target.Slice(32 - bytesToWrite, bytesToWrite);
            }

            value.TryWriteBytes(target, out int bytesWritten, true, true);
        }

        [Benchmark]
        public void UInt256()
        {
            Span<byte> span = _stack.AsSpan();
            Dirichlet.Numerics.UInt256.CreateFromBigEndian(out UInt256 a, span.Slice(0, 32));
            Dirichlet.Numerics.UInt256.CreateFromBigEndian(out UInt256 b, span.Slice(32, 32));
            Dirichlet.Numerics.UInt256.Add(out UInt256 c, ref a, ref b, false);
            c.ToBigEndian(span.Slice(0, 32));
        }

        [Benchmark]
        public void UInt256InPlace()
        {
            Span<byte> span = _stack.AsSpan();
            Dirichlet.Numerics.UInt256.AddInPlace(span.Slice(0, 32), span.Slice(32, 32));
        }
    }
}