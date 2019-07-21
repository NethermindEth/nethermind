/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    public static class DbExtensions
    {
        public static void Set(this IDb db, Keccak key, byte[] value)
        {
            db[key.Bytes] = value;
        }
        
        public static byte[] Get(this IDb db, Keccak key)
        {
            #if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
            #endif
            
            return db[key.Bytes];
        }
        
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
        
        public static void Set(this IDb db, long key, byte[] value)
        {
            db[key.ToBigEndianByteArrayWithoutLeadingZeros()] = value;
        }
        
        public static byte[] Get(this IDb db, long key)
        {
            return db[key.ToBigEndianByteArrayWithoutLeadingZeros()];
        }
        
        public static void Delete(this IDb db, long key)
        {
            db.Remove(key.ToBigEndianByteArrayWithoutLeadingZeros());
        }
    }
}