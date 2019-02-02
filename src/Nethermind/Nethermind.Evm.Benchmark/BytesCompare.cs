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
using System.Linq;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class BytesCompare
    {
        private static Random _random = new Random(0);

        private byte[] _a;
        private byte[] _b;

        [Params(true, false)] public bool AllZeros { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = new byte[64];
            _b = new byte[64];
            if (!AllZeros)
            {
                _random.NextBytes(_a);
                _a.CopyTo(_b.AsSpan());
            }
        }
        
        [Benchmark]
        public bool Improved()
        {
            throw new NotImplementedException();
        }
        
        [Benchmark]
        public bool Current()
        {
            return Bytes.AreEqual(_a, _b);
        }
    }
}