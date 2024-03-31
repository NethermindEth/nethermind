// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public static class KeyValueStoreRlpExtensions
{
    [SkipLocalsInit]
    public static TItem? Get<TItem>(this IReadOnlyKeyValueStore db, long blockNumber, ValueHash256 hash, IRlpStreamDecoder<TItem> decoder,
        ConcurrentLruCache<ValueHash256, TItem> cache = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = true) where TItem : class
    {
        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, hash, dbKey);
        return Get(db, hash, dbKey, decoder, cache, rlpBehaviors, shouldCache);
    }

    public static TItem? Get<TItem>(this IReadOnlyKeyValueStore db, ValueHash256 key, IRlpStreamDecoder<TItem> decoder, ConcurrentLruCache<ValueHash256, TItem> cache = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = true) where TItem : class
    {
        return Get(db, key, key.Bytes, decoder, cache, rlpBehaviors, shouldCache);
    }

    public static TItem? Get<TItem>(this IReadOnlyKeyValueStore db, long key, IRlpStreamDecoder<TItem>? decoder, NonBlockingLruCache<long, TItem>? cache = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = true) where TItem : class
    {
        byte[] keyDb = key.ToBigEndianByteArrayWithoutLeadingZeros();
        return Get(db, key, keyDb, decoder, cache, rlpBehaviors, shouldCache);
    }

    public static TItem? Get<TCacheKey, TItem>(
        this IReadOnlyKeyValueStore db,
        TCacheKey cacheKey,
        ReadOnlySpan<byte> key,
        IRlpStreamDecoder<TItem> decoder,
        ICache<TCacheKey, TItem> cache = null,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None,
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
                    item = valueDecoder.Decode(ref rlpValueContext, rlpBehaviors | RlpBehaviors.AllowExtraBytes);
                }
                finally
                {
                    db.DangerousReleaseMemory(data);
                }
            }
            else
            {
                Span<byte> data = db.Get(key);
                if (data.IsNull())
                {
                    return null;
                }

                IByteBuffer buff = PooledByteBufferAllocator.Default.Buffer(data.Length);
                data.CopyTo(buff.Array.AsSpan(buff.ArrayOffset + buff.WriterIndex));
                buff.SetWriterIndex(buff.WriterIndex + data.Length);

                using NettyRlpStream nettyRlpStream = new NettyRlpStream(buff);

                item = decoder.Decode(nettyRlpStream, rlpBehaviors | RlpBehaviors.AllowExtraBytes);
            }
        }

        if (shouldCache && cache is not null && item is not null)
        {
            cache.Set(cacheKey, item);
        }

        return item;
    }
}
