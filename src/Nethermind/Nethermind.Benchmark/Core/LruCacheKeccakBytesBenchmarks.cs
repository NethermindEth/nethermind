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
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core
{
    [EvaluateOverhead(false)]
    public class LruCacheKeccakBytesBenchmarks
    {
        [GlobalSetup]
        public void InitKeccaks()
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                Keys[i] = Keccak.Compute(i.ToString());
            }
        }

        [Params(0, 2, 4, 8, 16, 32)]
        public int StartCapacity { get; set; }

        [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28)]
        public int ItemsCount { get; set; }

        public Keccak[] Keys { get; set; } = new Keccak[28];

        public byte[] Value { get; set; } = new byte[0];

        [Benchmark]
        public LruCache<Keccak, byte[]> WithItems()
        {
            LruCache<Keccak, byte[]> cache = new LruCache<Keccak, byte[]>(128, StartCapacity, String.Empty);
            for (int j = 0; j < ItemsCount; j++)
            {
                cache.Set(Keys[j], Value);
            }

            return cache;
        }
    }
}
