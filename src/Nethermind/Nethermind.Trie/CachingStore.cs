// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;

namespace Nethermind.Trie
{
    public static class KeyValueStoreWithBatchingExtensions
    {
        public static CachingStore Cached(this IKeyValueStoreWithBatching @this, int maxCapacity)
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
            _cache = new LruCache<ArraySegment<byte>, byte[]>(maxCapacity, "RLP Cache");
        }

        private readonly LruCache<ArraySegment<byte>, byte[]> _cache;

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get
            {
                ArraySegment<byte> keyAsArray = new(ArrayPool<byte>.Shared.Rent(key.Length), 0, key.Length);
                key.CopyTo(keyAsArray);
                if (!_cache.TryGet(keyAsArray, out byte[] value))
                {
                    value = _wrappedStore[key];
                    _cache.Set(keyAsArray, value);
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(keyAsArray.Array);
                    // TODO: a hack assuming that we cache only one thing, accepted unanimously by Lukasz, Marek, and Tomasz
                    Pruning.Metrics.LoadedFromRlpCacheNodesCount++;
                }

                return value;
            }
            set
            {
                _cache.Set(key.ToArray(), value);
                _wrappedStore[key] = value;
            }
        }

        public IBatch StartBatch() => _wrappedStore.StartBatch();

        public void PersistCache(IKeyValueStore pruningContext)
        {
            IDictionary<ArraySegment<byte>, byte[]> clone = _cache.Clone();
            Task.Run(() =>
            {
                foreach (KeyValuePair<ArraySegment<byte>, byte[]> kvp in clone)
                {
                    pruningContext[kvp.Key] = kvp.Value;
                }
            });
        }
    }
}
