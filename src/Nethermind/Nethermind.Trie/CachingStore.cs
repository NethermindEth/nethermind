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
// 

using System;
using Nethermind.Core.Caching;

namespace Nethermind.Trie
{
    public class CachingStore : IKeyValueStore
    {
        private readonly IKeyValueStore _wrappedStore;

        public CachingStore(IKeyValueStore wrappedStore, int maxCapacity)
        {
            _wrappedStore = wrappedStore ?? throw new ArgumentNullException(nameof(wrappedStore));
            _cache = new LruCache<byte[], byte[]>(maxCapacity, "RLP Cache");
        }

        private LruCache<byte[], byte[]> _cache;

        public byte[] this[byte[] key]
        {
            get
            {
                byte[] value;
                if (_cache.Contains(key))
                {
                    value = _cache.Get(key);
                }
                else
                {
                    value = _wrappedStore[key];
                    _cache.Set(key, value);
                }

                return value;
            }
            set
            {
                _cache.Set(key, value);
                _wrappedStore[key] = value;
            }
        }
    }
}