// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Caching;

namespace Nethermind.Benchmarks.Core
{
    [EvaluateOverhead(false)]
    public class LruCacheBenchmarks
    {
        [Params(0, 4, 16, 32)]
        public int StartCapacity { get; set; }

        [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20)]
        public int ItemsCount { get; set; }

        [Benchmark]
        public LruCache<int, object> WithItems()
        {
            LruCache<int, object> cache = new LruCache<int, object>(16, StartCapacity, string.Empty);
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
}
