// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public static class KeyValueStoreRlpExtensions
{
    [SkipLocalsInit]
    public static TItem? Get<TItem>(this IReadOnlyKeyValueStore db, long blockNumber, ValueKeccak hash, IRlpStreamDecoder<TItem> decoder,
        LruCache<ValueKeccak, TItem> cache = null, bool shouldCache = true) where TItem : class
    {
        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, hash, dbKey);
        return Get(db, hash, dbKey, decoder, cache, shouldCache);
    }

    public static TItem? Get<TItem>(this IReadOnlyKeyValueStore db, Keccak key, IRlpStreamDecoder<TItem> decoder, LruCache<ValueKeccak, TItem> cache = null, bool shouldCache = true) where TItem : class
    {
        return Get(db, key, key.Bytes, decoder, cache, shouldCache);
    }

    public static TItem? Get<TItem>(this IReadOnlyKeyValueStore db, long key, IRlpStreamDecoder<TItem>? decoder, LruCache<long, TItem>? cache = null, bool shouldCache = true) where TItem : class
    {
        byte[] keyDb = key.ToBigEndianByteArrayWithoutLeadingZeros();
        return Get(db, key, keyDb, decoder, cache, shouldCache);
    }

    public static TItem? Get<TCacheKey, TItem>(
        this IReadOnlyKeyValueStore db,
        TCacheKey cacheKey,
        Span<byte> key,
        IRlpStreamDecoder<TItem> decoder,
        LruCache<TCacheKey, TItem> cache = null,
        bool shouldCache = true
    ) where TItem : class
    {
        TItem item = cache?.Get(cacheKey);
        if (item is null)
        {
            if (decoder is IRlpValueDecoder<TItem> valueDecoder)
            {
                Span<byte> data = db.GetSpan(key);
                if (data.IsNull())
                {
                    return null;
                }

                try
                {
                    if (data.Length == 0)
                    {
                        return null;
                    }

                    var rlpValueContext = data.AsRlpValueContext();
                    item = valueDecoder.Decode(ref rlpValueContext, RlpBehaviors.AllowExtraBytes);
                }
                finally
                {
                    db.DangerousReleaseMemory(data);
                }
            }
            else
            {
                byte[]? data = db.Get(key);
                if (data is null)
                {
                    return null;
                }

                item = decoder.Decode(data.AsRlpStream(), RlpBehaviors.AllowExtraBytes);
            }
        }

        if (shouldCache && cache is not null && item is not null)
        {
            cache.Set(cacheKey, item);
        }

        return item;
    }
}
