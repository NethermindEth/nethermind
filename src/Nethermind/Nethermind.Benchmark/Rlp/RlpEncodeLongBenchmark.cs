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
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Microsoft.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Benchmarks.Rlp
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class RlpEncodeLongBenchmark
    {
        private long[] _scenarios;

        public RlpEncodeLongBenchmark()
        {
            _scenarios = new[]
            {
                long.MinValue,
                -1,
                0,
                1,
                128,
                256,
                256 * 256,
                256 * 256 * 256,
                256 * 256 * 256 * 256L,
                256 * 256 * 256 * 256L * 256L,
                256 * 256 * 256 * 256L * 256 * 256,
                256 * 256 * 256 * 256L * 256 * 256 * 256,
                long.MaxValue
            };
        }

        private long _value;

        [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _value = _scenarios[ScenarioIndex];
            
            Console.WriteLine($"Length current: {Current().Length}");
            Console.WriteLine($"Length improved: {Improved().Length}");
            Check(Current().Bytes, Improved().Bytes);
        }
        
        private void Check(byte[] a, byte[] b)
        {
            if (!a.SequenceEqual(b))
            {
                Console.WriteLine($"Outputs are different {a.ToHexString()} != {b.ToHexString()}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a.ToHexString()}");
        }

        [Benchmark]
        public Nethermind.Core.Encoding.Rlp Improved()
        {
            return Nethermind.Core.Encoding.Rlp.Encode(_value);
        }

        [Benchmark]
        public Nethermind.Core.Encoding.Rlp Current()
        {
            return Nethermind.Core.Encoding.Rlp.Encode(_value);
        }
    }
}