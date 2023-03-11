// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;

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
            _cache = new SpanLruCache<byte, byte[]>(maxCapacity, 0, "RLP Cache", Bytes.SpanEqualityComparer);
        }

        private readonly SpanLruCache<byte, byte[]> _cache;

        public byte[]? this[ReadOnlySpan<byte> key]
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

        public IBatch StartBatch() => _wrappedStore.StartBatch();

        public void PersistCache(IKeyValueStore pruningContext)
        {
            KeyValuePair<byte[], byte[]>[] clone = _cache.ToArray();
            Task.Run(() =>
            {
                foreach (KeyValuePair<byte[], byte[]> kvp in clone)
                {
                    pruningContext[kvp.Key] = kvp.Value;
                }
            });
        }
    }
}
