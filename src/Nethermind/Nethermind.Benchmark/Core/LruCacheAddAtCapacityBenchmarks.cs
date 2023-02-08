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
        private ICache<int, object> previous;


        [GlobalSetup]
        public void Setup()
        {
            shared = new LruCache<int, object>(Capacity, Capacity, string.Empty);
            previous = new PreviousLruCache<int, object>(Capacity, Capacity, string.Empty);
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

        [Benchmark(Baseline = true)]
        public ICache<int, object> WithRecreation_Previous()
        {
            ICache<int, object> cache = new PreviousLruCache<int, object>(Capacity, Capacity, string.Empty);
            Fill(cache);

            return cache;

            void Fill(ICache<int, object> cache)
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

        [Benchmark(Baseline = true)]
        public void WithClear_Previous()
        {
            for (int j = 0; j < 1024 * 64; j++)
            {
                previous.Set(j, _object);
            }

            previous.Clear();
        }
    }
}
