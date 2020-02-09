﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Extensions
{
    public static class SpanExtensions
    {
        public static string ToHexString(this Span<byte> span, bool withZeroX)
        {
            return ToHexString(span, withZeroX, false, false);
        }
        
        public static string ToHexString(this Span<byte> span)
        {
            return ToHexString(span, false, false, false);
        }
        
        public static string ToHexString(this Span<byte> span, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ByteArrayToHexViaLookup32(span, withZeroX, noLeadingZeros, withEip55Checksum);
        }
        
        [DebuggerStepThrough]
        private static string ByteArrayToHexViaLookup32(Span<byte> span, bool withZeroX, bool skipLeadingZeros,
            bool withEip55Checksum)
        {
            int leadingZeros = skipLeadingZeros ? CountLeadingZeros(span) : 0;
            char[] result = new char[span.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros];
            string hashHex = null;
            if (withEip55Checksum)
            {
                hashHex = Keccak.Compute(span.ToHexString(false)).ToString(false);
            }

            if (withZeroX)
            {
                result[0] = '0';
                result[1] = 'x';
            }

            for (int i = 0; i < span.Length; i++)
            {
                uint val = Lookup32[span[i]];
                char char1 = (char) val;
                char char2 = (char) (val >> 16);

                if (leadingZeros <= i * 2)
                {
                    result[2 * i + (withZeroX ? 2 : 0) - leadingZeros] =
                        withEip55Checksum && char.IsLetter(char1) && hashHex[2 * i] > '7'
                            ? char.ToUpper(char1)
                            : char1;
                }

                if (leadingZeros <= i * 2 + 1)
                {
                    result[2 * i + 1 + (withZeroX ? 2 : 0) - leadingZeros] =
                        withEip55Checksum && char.IsLetter(char2) && hashHex[2 * i + 1] > '7'
                            ? char.ToUpper(char2)
                            : char2;
                }
            }

            if (skipLeadingZeros && result.Length == (withZeroX ? 2 : 0))
            {
                return withZeroX ? "0x0" : "0";
            }

            return new string(result);
        }
        
        private static readonly uint[] Lookup32 = CreateLookup32("x2");

        private static uint[] CreateLookup32(string format)
        {
            uint[] result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString(format);
                result[i] = s[0] + ((uint) s[1] << 16);
            }

            return result;
        }
        
        private static int CountLeadingZeros(Span<byte> span)
        {
            int leadingZeros = 0;
            for (int i = 0; i < span.Length; i++)
            {
                if ((span[i] & 240) == 0)
                {
                    leadingZeros++;
                    if ((span[i] & 15) == 0)
                    {
                        leadingZeros++;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            return leadingZeros;
        }        
    }
    
    public static class ByteArrayExtensions
    {
        public static byte[] Xor(this byte[] bytes, byte[] otherBytes)
        {
            if (bytes.Length != otherBytes.Length)
            {
                throw new InvalidOperationException($"Trying to xor arrays of different lengths: {bytes.Length} and {otherBytes.Length}");
            }
            
            byte[] result = new byte[bytes.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte)(bytes[i] ^ otherBytes[i]);
            }

            return result;
        }
        
        public static byte[] Xor(this Span<byte> bytes, Span<byte> otherBytes)
        {
            if (bytes.Length != otherBytes.Length)
            {
                throw new InvalidOperationException($"Trying to xor arrays of different lengths: {bytes.Length} and {otherBytes.Length}");
            }
            
            byte[] result = new byte[bytes.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte)(bytes[i] ^ otherBytes[i]);
            }

            return result;
        }
        
        public static void XorInPlace(this byte[] bytes, byte[] otherBytes)
        {
            if (bytes.Length != otherBytes.Length)
            {
                throw new InvalidOperationException($"Trying to xor arrays of different lengths: {bytes?.Length} and {otherBytes?.Length}");
            }
            
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(bytes[i] ^ otherBytes[i]);
            }
        }

        public static byte[] Slice(this byte[] bytes, int startIndex)
        {
            byte[] slice = new byte[bytes.Length - startIndex];
            Buffer.BlockCopy(bytes, startIndex, slice, 0, bytes.Length - startIndex);
            return slice;
        }

        public static byte[] Slice(this byte[] bytes, int startIndex, int length)
        {
            if (length == 1)
            {
                return new[] {bytes[startIndex]};
            }

            byte[] slice = new byte[length];
            Buffer.BlockCopy(bytes, startIndex, slice, 0, length);
            return slice;
        }

        public static byte[] SliceWithZeroPadding(this Span<byte> bytes, int startIndex, int length)
        {
            if (startIndex >= bytes.Length)
            {
                return new byte[length];
            }

            if (length == 1)
            {
                return bytes.Length == 0 ? new byte[0] : new[] {bytes[startIndex]};
            }

            byte[] result = new byte[length];
            int copiedLength = Math.Min(bytes.Length - startIndex, length);
            bytes.Slice(startIndex, copiedLength).CopyTo(result.AsSpan().Slice(0, copiedLength));
//            Buffer.BlockCopy(bytes.ToArray(), startIndex, result, 0, Math.Min(bytes.Length - startIndex, length));
            return result;
        }

        public static byte[] SliceWithZeroPadding(this Span<byte> bytes, UInt256 startIndex, int length)
        {
            if (startIndex >= bytes.Length || startIndex > int.MaxValue)
            {
                return new byte[length];
            }

            return SliceWithZeroPadding(bytes, (int) startIndex, length);
        }
        
        public static byte[] SliceWithZeroPadding(this byte[] bytes, UInt256 startIndex, int length)
        {
            return bytes.AsSpan().SliceWithZeroPadding(startIndex, length);
        }
        
        public static byte[] SliceWithZeroPadding(this byte[] bytes, int startIndex, int length)
        {
            return bytes.AsSpan().SliceWithZeroPadding(startIndex, length);
        }

        public static byte[] SliceWithZeroPaddingEmptyOnError(this byte[] bytes, BigInteger startIndex, int length)
        {
            if (startIndex >= bytes.Length || length == 0)
            {
                return new byte[0];
            }

            if (length == 1)
            {
                return bytes.Length == 0 ? new byte[0] : new[] {bytes[(int)startIndex]};
            }

            byte[] slice = new byte[length];
            if (startIndex > bytes.Length - 1)
            {
                return slice;
            }

            Buffer.BlockCopy(bytes, (int)startIndex, slice, 0, Math.Min(bytes.Length - (int)startIndex, length));
            return slice;
        }

        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public static int GetSimplifiedHashCode(this byte[] bytes)
        {
            const int fnvPrime = 0x01000193;

            if (bytes.Length == 0)
            {
                return 0;
            }

            return (fnvPrime * bytes.Length * (((fnvPrime * (bytes[0] + 7)) ^ (bytes[^1] + 23)) + 11)) ^ (bytes[(bytes.Length - 1) / 2] + 53);
        }
    }
}