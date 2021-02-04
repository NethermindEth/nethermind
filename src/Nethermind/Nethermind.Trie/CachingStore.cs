//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using Nethermind.Core;
using Nethermind.Core.Caching;

namespace Nethermind.Trie
{
    public static class KeyValueStoreWithBatchingExtensions
    {
        public static IKeyValueStoreWithBatching Cached(this IKeyValueStoreWithBatching @this, int maxCapacity)
        {
            return new CachingStore(@this, maxCapacity);
        }
    }

    public class CachingStore : IKeyValueStoreWithBatching
    {
        private readonly IKeyValueStoreWithBatching _wrappedStore;

        public CachingStore(IKeyValueStoreWithBatching wrappedStore, int maxCapacity)
        {
            _wrappedStore = wrappedStore ?? throw new ArgumentNullException(nameof(wrappedStore));
            _cache = new LruCache<byte[], byte[]>(maxCapacity, "RLP Cache");
        }

        private readonly LruCache<byte[], byte[]> _cache;

        public byte[]? this[byte[] key]
        {
            get
            {
                if (!_cache.TryGet(key, out byte[] value))
                {
                    value = _wrappedStore[key];
                    _cache.Set(key, value);
                }
                else
                {
                    // TODO: a hack assuming that we cache only one thing, accepted unanimously by Lukasz, Marek, and Tomasz
                    Pruning.Metrics.LoadedFromRlpCacheNodesCount++;
                }

                return value;
            }
            set
            {
                _cache.Set(key, value);
                _wrappedStore[key] = value;
            }
        }

        public IBatch StartBatch()
        {
            return _wrappedStore.StartBatch();
        }
    }
}
