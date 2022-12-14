// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
