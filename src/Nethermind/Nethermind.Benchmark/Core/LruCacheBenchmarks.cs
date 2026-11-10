// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Caching;

namespace Nethermind.Benchmarks.Core
{
    public class LruCacheBenchmarks
    {
        [Params(0, 4, 16, 32)]
        public int StartCapacity { get; set; }

        [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20)]
        public int ItemsCount { get; set; }

        [Benchmark]
        public LruCache<int, object> WithItems()
        {
            LruCache<int, object> cache = new(16, StartCapacity, string.Empty);
            Fill(cache);

            return cache;

            void Fill(LruCache<int, object> cache)
            {
                for (int j = 0; j < ItemsCount; j++)
                {
                    cache.Set(j, new object());
                }
            }
        }
    }

    [MemoryDiagnoser]
    public class LruCacheReuseBenchmarks
    {
        private const int Operations = 16384;
        private readonly object _value = new();
        private LruCache<int, object> _cache = null!;

        [Params(1024, 16384)]
        public int Capacity { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _cache = new(Capacity, Capacity, nameof(LruCacheReuseBenchmarks));
            for (int i = 0; i < Capacity; i++) _cache.Set(i, _value);
        }

        [Benchmark(OperationsPerInvoke = Operations)]
        public void DeleteRefill()
        {
            for (int i = 0; i < Operations; i++)
            {
                int key = i % Capacity;
                _cache.Delete(key);
                _cache.Set(key, _value);
            }
        }

        [Benchmark(OperationsPerInvoke = Operations)]
        public void ClearRefill()
        {
            for (int i = 0; i < Operations; i++)
            {
                int key = i % Capacity;
                if (key == 0) _cache.Clear();
                _cache.Set(key, _value);
            }
        }
    }
}
