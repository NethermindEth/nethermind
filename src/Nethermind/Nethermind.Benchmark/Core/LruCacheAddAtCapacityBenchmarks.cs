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
            for (int j = 0; j < 1024 * 64; j++)
            {
                cache.Set(j, _object);
            }

            return cache;
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
