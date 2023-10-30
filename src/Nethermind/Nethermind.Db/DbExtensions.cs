// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Db
{
    public static class DbExtensions
    {
        public static ReadOnlyDb AsReadOnly(this IDb db, bool createInMemoryWriteStore)
        {
            return new(db, createInMemoryWriteStore);
        }

        public static void Set(this IDb db, Hash256 key, byte[] value, WriteFlags writeFlags = WriteFlags.None)
        {
            db.Set(key.Bytes, value, writeFlags);
        }

        public static byte[]? Get(this IDb db, Hash256 key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
#endif

            return db[key.Bytes];
        }

        public static void Set(this IDb db, Hash256 key, Span<byte> value)
        {
            if (db is IDbWithSpan dbWithSpan)
            {
                dbWithSpan.PutSpan(key.Bytes, value);
            }
            else
            {
                db[key.Bytes] = value.ToArray();
            }
        }

        public static void Set(this IDb db, long blockNumber, Hash256 key, Span<byte> value, WriteFlags writeFlags = WriteFlags.None)
        {
            Span<byte> blockNumberPrefixedKey = stackalloc byte[40];
            GetBlockNumPrefixedKey(blockNumber, key, blockNumberPrefixedKey);
            db.Set(blockNumberPrefixedKey, value, writeFlags);
        }

        private static void GetBlockNumPrefixedKey(long blockNumber, ValueHash256 blockHash, Span<byte> output)
        {
            blockNumber.WriteBigEndian(output);
            blockHash!.Bytes.CopyTo(output[8..]);
        }

        public static void Set(this IDb db, in ValueHash256 key, Span<byte> value)
        {
            if (db is IDbWithSpan dbWithSpan)
            {
                dbWithSpan.PutSpan(key.Bytes, value);
            }
            else
            {
                db[key.Bytes] = value.ToArray();
            }
        }

        public static void Set(this IDb db, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None)
        {
            if (db is IDbWithSpan dbWithSpan)
            {
                dbWithSpan.PutSpan(key, value, flags: writeFlags);
            }
            else
            {
                db.Set(key, value.ToArray(), flags: writeFlags);
            }
        }

        public static KeyValuePair<byte[], byte[]>[] MultiGet(this IDb db, IEnumerable<ValueHash256> keys)
        {
            var k = keys.Select(k => k.Bytes.ToArray()).ToArray();
            return db[k];
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="db"></param>
        /// <param name="key"></param>
        /// <returns>Can return null or empty Span on missing key</returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static Span<byte> GetSpan(this IDbWithSpan db, Hash256 key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
#endif

            return db.GetSpan(key.Bytes);
        }

        public static bool KeyExists(this IDb db, Hash256 key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
#endif

            return db.KeyExists(key.Bytes);
        }

        public static bool KeyExists(this IDb db, long key)
        {
            return db.KeyExists(key.ToBigEndianByteArrayWithoutLeadingZeros());
        }

        public static void Delete(this IDb db, Hash256 key)
        {
            db.Remove(key.Bytes);
        }

        [SkipLocalsInit]
        public static void Delete(this IDb db, long blockNumber, Hash256 hash)
        {
            Span<byte> key = stackalloc byte[40];
            GetBlockNumPrefixedKey(blockNumber, hash, key);
            db.Remove(key);
        }

        public static void Set(this IDb db, byte[] key, byte[] value)
        {
            db[key] = value;
        }

        public static void Set(this IDb db, long key, byte[] value)
        {
            db[key.ToBigEndianByteArrayWithoutLeadingZeros()] = value;
        }

        public static byte[]? Get(this IDb db, long key) => db[key.ToBigEndianByteArrayWithoutLeadingZeros()];

        public static byte[]? Get(this IDb db, byte[] key) => db[key];

        /// <summary>
        ///
        /// </summary>
        /// <param name="db"></param>
        /// <param name="key"></param>
        /// <returns>Can return null or empty Span on missing key</returns>
        public static Span<byte> GetSpan(this IDbWithSpan db, long key) => db.GetSpan(key.ToBigEndianByteArrayWithoutLeadingZeros());


        public static void Delete(this IDb db, long key)
        {
            db.Remove(key.ToBigEndianByteArrayWithoutLeadingZeros());
        }

        [SkipLocalsInit]
        public static TItem? Get<TItem>(this IDb db, long blockNumber, ValueHash256 hash, IRlpStreamDecoder<TItem> decoder,
            LruCache<ValueHash256, TItem> cache = null, bool shouldCache = true) where TItem : class
        {
            Span<byte> dbKey = stackalloc byte[40];
            GetBlockNumPrefixedKey(blockNumber, hash, dbKey);
            return Get(db, hash, dbKey, decoder, cache, shouldCache);
        }

        public static TItem? Get<TItem>(this IDb db, Hash256 key, IRlpStreamDecoder<TItem> decoder, LruCache<ValueHash256, TItem> cache = null, bool shouldCache = true) where TItem : class
        {
            return Get(db, key, key.Bytes, decoder, cache, shouldCache);
        }

        public static TItem? Get<TItem>(this IDb db, long key, IRlpStreamDecoder<TItem>? decoder, LruCache<long, TItem>? cache = null, bool shouldCache = true) where TItem : class
        {
            byte[] keyDb = key.ToBigEndianByteArrayWithoutLeadingZeros();
            return Get(db, key, keyDb, decoder, cache, shouldCache);
        }

        public static TItem? Get<TCacheKey, TItem>(
            this IDb db,
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
                if (db is IDbWithSpan spanDb && decoder is IRlpValueDecoder<TItem> valueDecoder)
                {
                    Span<byte> data = spanDb.GetSpan(key);
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
                        spanDb.DangerousReleaseMemory(data);
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
}
