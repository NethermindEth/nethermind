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
    [MemoryDiagnoser]
    [SimpleJob]
    public class LruCacheAddAtCapacityBenchmarks
    {
        private object _object = new object();
        
        [Benchmark]
        public ICache<int, object> No_recycling()
        {
            LruCache<int, object> cache = new LruCache<int, object>(16, 16, string.Empty);
            for (int j = 0; j < 1024 * 64; j++)
            {
                cache.Set(j, _object);
            }

            return cache;
        }
        
        [Benchmark]
        public ICache<int, object> With_recycling()
        {
            LruCacheWithRecycling<int, object> cache = new LruCacheWithRecycling<int, object>(16, 16, string.Empty);
            for (int j = 0; j < 1024 * 64; j++)
            {
                cache.Set(j, _object);
            }

            return cache;
        }
    }
}