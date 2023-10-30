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

        [Params(16, 32, 128)]
        public int MaxCapacity { get; set; }

        [Params(1, 2, 8, 32, 64)]
        public int ItemsCount { get; set; }

        public Hash256[] Keys { get; set; } = new Hash256[64];

        public byte[] Value { get; set; } = new byte[0];

        [Benchmark]
        public LruCache<ValueHash256, byte[]> WithItems()
        {
            LruCache<ValueHash256, byte[]> cache = new LruCache<ValueHash256, byte[]>(MaxCapacity, MaxCapacity, String.Empty);
            Fill(cache);

            return cache;

            void Fill(LruCache<ValueHash256, byte[]> cache)
            {
                for (int j = 0; j < ItemsCount; j++)
                {
                    cache.Set(Keys[j], Value);
                }
            }
        }
    }
}
