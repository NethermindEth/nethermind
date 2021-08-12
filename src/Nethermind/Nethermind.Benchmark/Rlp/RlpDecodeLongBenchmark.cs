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
using BenchmarkDotNet.Jobs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeLongBenchmark
    {
        private long[] _scenarios;

        public RlpDecodeLongBenchmark()
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

        private byte[] _value;

        [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _value = Serialization.Rlp.Rlp.Encode(_scenarios[ScenarioIndex]).Bytes;
            
            Check(Current(), Improved());
        }
        
        private void Check(long a, long b)
        {
            if (a != b)
            {
                Console.WriteLine($"Outputs are different {a} != {b}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a}");
        }

        [Benchmark]
        public long Improved()
        {
            return new RlpStream(_value).DecodeLong();
        }

        [Benchmark]
        public long Current()
        {
            return new RlpStream(_value).DecodeLong();
        }
    }
}
