///*
// * Copyright (c) 2018 Demerzel Solutions Limited
// * This file is part of the Nethermind library.
// *
// * The Nethermind library is free software: you can redistribute it and/or modify
// * it under the terms of the GNU Lesser General Public License as published by
// * the Free Software Foundation, either version 3 of the License, or
// * (at your option) any later version.
// *
// * The Nethermind library is distributed in the hope that it will be useful,
// * but WITHOUT ANY WARRANTY; without even the implied warranty of
// * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// * GNU Lesser General Public License for more details.
// *
// * You should have received a copy of the GNU Lesser General Public License
// * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// */
//
//using System;
//using System.Diagnostics;
//using System.Diagnostics.CodeAnalysis;
//using Nethermind.Core.Crypto;
//using Nethermind.Core.Extensions;
//
//namespace Nethermind.Core
//{
//    /// <summary>
//    ///     https://stackoverflow.com/questions/311165/how-do-you-convert-a-byte-array-to-a-hexadecimal-string-and-vice-versa
//    /// </summary>
//    [DebuggerStepThrough]
//    public class Hex : IEquatable<Hex>
//    {
//        
//        private byte[] _bytes;
//        private string _hexString;
//
//        public Hex(string hexString)
//        {
//            _hexString = hexString.StartsWith("0x") ? hexString.Substring(2) : hexString;
//        }
//
//        public Hex(byte[] bytes)
//        {
//            _bytes = bytes;
//        }
//
//        public int ByteLength => _bytes?.Length ?? _hexString.Length / 2;
//        public int StringLength => _hexString?.Length ?? _bytes.Length * 2;
//
//        public bool Equals(Hex obj)
//        {
//            if (obj == null)
//            {
//                return false;
//            }
//
//            if (_bytes == null)
//            {
//                _bytes = ToBytes(_hexString);
//            }
//            
//            if (obj._bytes == null)
//            {
//                obj._bytes = ToBytes(obj._hexString);
//            }
//            
//            return Bytes.UnsafeCompare(_bytes, obj._bytes);
//        }
//
//        public override string ToString()
//        {
//            return ToString(true);
//        }
//        
//
//
//        public static implicit operator byte[](Hex hex)
//        {
//            return hex._bytes ?? (hex._bytes = ToBytes(hex._hexString));
//        }
//
//        public static implicit operator string(Hex hex)
//        {
//            return hex.ToString(false);
//        }
//
//        public static implicit operator Hex(string hex)
//        {
//            if (hex == null)
//            {
//                return null;
//            }
//
//            return new Hex(hex);
//        }
//
//        public static implicit operator Hex(byte[] bytes)
//        {
//            if (bytes == null)
//            {
//                return null;
//            }
//
//            return new Hex(bytes);
//        }
//
//        public override bool Equals(object obj)
//        {
//            if (!(obj is Hex))
//            {
//                return false;
//            }
//
//            return Equals((Hex)obj);
//        }
//
//        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
//        public override int GetHashCode()
//        {
//            const int fnvPrime = 0x01000193;
//
//            if (_bytes == null)
//            {
//                _bytes = ToBytes(_hexString);
//            }
//
//            if (_bytes.Length == 0)
//            {
//                return 0;
//            }
//
//            return (fnvPrime * (((fnvPrime * (_bytes[0] + 7)) ^ (_bytes[_bytes.Length - 1] + 23)) + 11)) ^ (_bytes[(_bytes.Length - 1) / 2] + 53);
//        }
//
//
//        
//    }
//}