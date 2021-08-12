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

using System;
using System.Collections.Generic;
using System.Linq;
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
        
        public static void Set(this IDb db, Keccak key, byte[] value)
         {
             db[key.Bytes] = value;
         }
        
        public static byte[]? Get(this IDb db, Keccak key)
        {
            #if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
            #endif
            
            return db[key.Bytes];
        }
        
        public static KeyValuePair<byte[], byte[]>[] MultiGet(this IDb db, IEnumerable<Keccak> keys)
        {
            var k = keys.Select(k => k.Bytes).ToArray();
            return db[k];
        }
        
        /// <summary>
        /// 
        /// </summary>
        /// <param name="db"></param>
        /// <param name="key"></param>
        /// <returns>Can return null or empty Span on missing key</returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static Span<byte> GetSpan(this IDbWithSpan db, Keccak key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
#endif
            
            return db.GetSpan(key.Bytes);
        }
        
        public static bool KeyExists(this IDb db, Keccak key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
#endif
            
            return db.KeyExists(key.Bytes);
        }
        
        public static void Delete(this IDb db, Keccak key)
        {
            db.Remove(key.Bytes);
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

        public static TItem? Get<TItem>(this IDb db, Keccak key, IRlpStreamDecoder<TItem> decoder, ICache<Keccak, TItem> cache = null, bool shouldCache = true) where TItem : class
        {
            TItem item = cache?.Get(key);
            if (item is null)
            {
                if (db is IDbWithSpan spanDb && decoder is IRlpValueDecoder<TItem> valueDecoder)
                {
                    Span<byte> data = spanDb.GetSpan(key);
                    if (data.IsNullOrEmpty())
                    {
                        return null;
                    }

                    try
                    {
                        var rlpValueContext = data.AsRlpValueContext();
                        item = valueDecoder.Decode(ref rlpValueContext, RlpBehaviors.AllowExtraData);
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

                    item = decoder.Decode(data.AsRlpStream(), RlpBehaviors.AllowExtraData);
                }
            }

            if (shouldCache && cache != null && item != null)
            {
                cache.Set(key, item);
            }
            
            return item;
        }
        
        public static TItem? Get<TItem>(this IDb db, long key, IRlpStreamDecoder<TItem>? decoder, ICache<long, TItem>? cache = null, bool shouldCache = true) where TItem : class
        {
            TItem? item = cache?.Get(key);
            if (item is null)
            {
                if (db is IDbWithSpan spanDb && decoder is IRlpValueDecoder<TItem> valueDecoder)
                {
                    Span<byte> data = spanDb.GetSpan(key);
                    if (data.IsNullOrEmpty())
                    {
                        return null;
                    }

                    try
                    {
                        var rlpValueContext = data.AsRlpValueContext();
                        item = valueDecoder.Decode(ref rlpValueContext, RlpBehaviors.AllowExtraData);
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

                    item = decoder.Decode(data.AsRlpStream(), RlpBehaviors.AllowExtraData);
                }
            }
            
            if (shouldCache && cache != null && item != null)
            {
                cache.Set(key, item);
            }

            return item;
        }
    }
}
