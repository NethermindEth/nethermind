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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Dirichlet.Benchmark
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class UInt256AddBenchmarks
    {
        private byte[] _stack = new byte[64];

        private (byte[] A, byte[] B)[] _scenarios = new[]
        {
            (Bytes.FromHexString("0x00"), Bytes.FromHexString("0x00")),
            (Bytes.FromHexString("0x00"), Bytes.FromHexString("0x01")),
            (Bytes.FromHexString("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFE"), Bytes.FromHexString("0x01")),
            (Bytes.FromHexString("0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), Bytes.FromHexString("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")),
            (Bytes.FromHexString("0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), Bytes.FromHexString("0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")),
            (Bytes.FromHexString("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"), Bytes.FromHexString("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")),
        };

        [Params(0, 1, 2, 3, 4, 5)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _scenarios[ScenarioIndex].A.PadLeft(32).AsSpan().CopyTo(_stack.AsSpan().Slice(0, 32));
            _scenarios[ScenarioIndex].B.PadLeft(32).AsSpan().CopyTo(_stack.AsSpan().Slice(32, 64));
            Current();
            var current = _stack.Clone() as byte[];
            Improved();
            var improved = _stack.Clone() as byte[];
            
            if (!Bytes.AreEqual(current, improved))
            {
                throw new InvalidBenchmarkDeclarationException($"{current.ToHexString()} != {improved.ToHexString()}");
            }
        }

        [Benchmark]
        public void Current()
        {
            Span<byte> span = _stack.AsSpan();
            UInt256.CreateFromBigEndian(out UInt256 a, span.Slice(0, 32));
            UInt256.CreateFromBigEndian(out UInt256 b, span.Slice(32, 32));
            UInt256.Add(out UInt256 c, ref a, ref b, false);
            c.ToBigEndian(span.Slice(0, 32));
        }

        [Benchmark]
        public void Improved()
        {
            Span<byte> span = _stack.AsSpan();
            UInt256.AddInPlace(span.Slice(0, 32), span.Slice(32, 32));
        }
    }
}