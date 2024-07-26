// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Caching;

namespace Nethermind.Benchmarks.Core
{
    public class LruCacheAddAtCapacityBenchmarks
    {
        const int Capacity = 16;
        private object _object = new object();
        private LruCache<int, object> shared;

        [GlobalSetup]
        public void Setup()
        {
            shared = new LruCache<int, object>(Capacity, Capacity, string.Empty);
        }

        [Benchmark]
        public ICache<int, object> WithRecreation()
        {
            LruCache<int, object> cache = new LruCache<int, object>(Capacity, Capacity, string.Empty);
            Fill(cache);

            return cache;

            void Fill(LruCache<int, object> cache)
            {
                for (int j = 0; j < 1024 * 64; j++)
                {
                    cache.Set(j, _object);
                }
            }
        }

        [Benchmark]
        public void WithClear()
        {
            for (int j = 0; j < 1024 * 64; j++)
            {
                shared.Set(j, _object);
            }

            shared.Clear();
        }
    }
}
