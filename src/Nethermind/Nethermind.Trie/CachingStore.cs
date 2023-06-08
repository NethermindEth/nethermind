// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

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
            _cache = new LruCache<ValueKeccak, byte[]>(maxCapacity, 0, "RLP Cache");
        }

        private readonly LruCache<ValueKeccak, byte[]> _cache;

        public byte[]? this[in ValueKeccak key]
        {
            get
            {
                return Get(key);
            }
            set
            {
                Set(key, value);
            }
        }
        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get
            {
                return Get(key);
            }
            set
            {
                Set(key, value);
            }
        }

        public byte[]? Get(in ValueKeccak key, ReadFlags flags = ReadFlags.None)
        {
            if ((flags & ReadFlags.HintCacheMiss) == ReadFlags.HintCacheMiss)
            {
                return _wrappedStore.Get(key, flags);
            }

            if (!_cache.TryGet(key, out byte[] value))
            {
                value = _wrappedStore.Get(key, flags);
                _cache.Set(key, value);
            }
            else
            {
                // TODO: a hack assuming that we cache only one thing, accepted unanimously by Lukasz, Marek, and Tomasz
                Pruning.Metrics.LoadedFromRlpCacheNodesCount++;
            }

            return value;
        }

        public void Set(in ValueKeccak key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _cache.Set(key, value);
            _wrappedStore.Set(key, value, flags);
        }

        public IBatch StartBatch() => _wrappedStore.StartBatch();

        public void PersistCache(IKeccakValueStore pruningContext)
        {
            KeyValuePair<ValueKeccak, byte[]>[] clone = _cache.ToArray();
            Task.Run(() =>
            {
                foreach (KeyValuePair<ValueKeccak, byte[]> kvp in clone)
                {
                    pruningContext[kvp.Key] = kvp.Value;
                }
            });
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (key.Length > ValueKeccak.MemorySize)
            {
                ThrowArgumentException(key.Length);
            }

            Span<byte> keySpan = stackalloc byte[ValueKeccak.MemorySize];
            key.CopyTo(keySpan[(ValueKeccak.MemorySize - key.Length)..]);

            _cache.Set(new ValueKeccak(keySpan), value);
            _wrappedStore.Set(key, value, flags);
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (key.Length > ValueKeccak.MemorySize)
            {
                ThrowArgumentException(key.Length);
            }

            Span<byte> keySpan = stackalloc byte[ValueKeccak.MemorySize];
            key.CopyTo(keySpan[(ValueKeccak.MemorySize - key.Length)..]);

            if ((flags & ReadFlags.HintCacheMiss) == ReadFlags.HintCacheMiss)
            {
                return _wrappedStore.Get(key, flags);
            }

            ValueKeccak keccak = new ValueKeccak(keySpan);
            if (!_cache.TryGet(keccak, out byte[] value))
            {
                value = _wrappedStore.Get(key, flags);
                _cache.Set(keccak, value);
            }
            else
            {
                // TODO: a hack assuming that we cache only one thing, accepted unanimously by Lukasz, Marek, and Tomasz
                Pruning.Metrics.LoadedFromRlpCacheNodesCount++;
            }

            return value;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowArgumentException(int length)
        {
            throw new ArgumentException($"Keccak hash must be {ValueKeccak.MemorySize} bytes long; is {length} bytes.");
        }
    }
}
