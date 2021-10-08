//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core2
{
    public static class Bytes
    {
        public static string ToHexString(this byte[] bytes, bool withZeroX)
        {
            return ToHexString(bytes.AsSpan(), withZeroX, false);
        }
        
        public static string ToHexString(this ReadOnlySpan<byte> span)
        {
            return ToHexString(span, false, false);
        }

        public static string ToHexString(this ReadOnlySpan<byte> span, bool withZeroX)
        {
            return ToHexString(span, withZeroX, false);
        }
        
        public static string ToHexString(this ReadOnlySpan<byte> span, bool withZeroX, bool noLeadingZeros)
        {
            return ByteArrayToHexViaLookup32(span, withZeroX, noLeadingZeros);
        }
        
        [DebuggerStepThrough]
        private static string ByteArrayToHexViaLookup32(ReadOnlySpan<byte> span, bool withZeroX, bool skipLeadingZeros)
        {
            int leadingZeros = skipLeadingZeros ? CountLeadingZeros(span) : 0;
            char[] result = new char[span.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros];
          
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
                    result[2 * i + (withZeroX ? 2 : 0) - leadingZeros] = char1;
                }

                if (leadingZeros <= i * 2 + 1)
                {
                    result[2 * i + 1 + (withZeroX ? 2 : 0) - leadingZeros] = char2;
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
        
        private static int CountLeadingZeros(ReadOnlySpan<byte> span)
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

        public static byte[] Xor(this ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> otherBytes)
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
      
        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public static int GetSimplifiedHashCode(this byte[] bytes)
        {
            const int fnvPrime = 0x01000193;

            if (bytes.Length == 0)
            {
                return 0;
            }

            return (fnvPrime * (((fnvPrime * (bytes[0] + 7)) ^ (bytes[^1] + 23)) + 11)) ^ (bytes[(bytes.Length - 1) / 2] + 53);
        }
        
         public static readonly IEqualityComparer<byte[]> EqualityComparer = new BytesEqualityComparer();

        public static readonly IComparer<byte[]> Comparer = new BytesComparer();

        private class BytesEqualityComparer : EqualityComparer<byte[]>
        {
            public override bool Equals(byte[] x, byte[] y)
            {
                return AreEqual(x, y);
            }

            public override int GetHashCode(byte[] obj)
            {
                return obj.GetSimplifiedHashCode();
            }
        }

        private class BytesComparer : Comparer<byte[]>
        {
            public override int Compare(byte[] x, byte[] y)
            {
                if (x == null)
                {
                    return y == null ? 0 : 1;
                }

                if (y == null)
                {
                    return -1;
                }

                if (x.Length == 0)
                {
                    return y.Length == 0 ? 0 : 1;
                }

                for (int i = 0; i < x.Length; i++)
                {
                    if (y.Length <= i)
                    {
                        return -1;
                    }

                    int result = x[i].CompareTo(y[i]);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return y.Length > x.Length ? 1 : 0;
            }
        }

        public static bool AreEqual(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
        {
            return a1.SequenceEqual(a2);
        }
        
        private static byte[] FromHexNibble1Table =
        {
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 0, 16,
            32, 48, 64, 80, 96, 112, 128, 144, 255, 255,
            255, 255, 255, 255, 255, 160, 176, 192, 208, 224,
            240, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 160, 176, 192,
            208, 224, 240
        };

        private static byte[] FromHexNibble2Table =
        {
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 0, 1,
            2, 3, 4, 5, 6, 7, 8, 9, 255, 255,
            255, 255, 255, 255, 255, 10, 11, 12, 13, 14,
            15, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 10, 11, 12,
            13, 14, 15
        };

        [DebuggerStepThrough]
        public static byte[] FromHexString(string hexString)
        {
            if (hexString == null)
            {
                throw new ArgumentNullException($"{nameof(hexString)}");
            }

            int startIndex = hexString.StartsWith("0x") ? 2 : 0;
            bool odd = hexString.Length % 2 == 1;
            int numberChars = hexString.Length - startIndex + (odd ? 1 : 0);
            byte[] bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2)
            {
                if (odd && i == 0)
                {
                    bytes[0] += FromHexNibble2Table[(byte) hexString[startIndex]];
                }
                else if (odd)
                {
                    bytes[i / 2] += FromHexNibble1Table[(byte) hexString[i + startIndex - 1]];
                    bytes[i / 2] += FromHexNibble2Table[(byte) hexString[i + startIndex]];
                }
                else
                {
                    bytes[i / 2] += FromHexNibble1Table[(byte) hexString[i + startIndex]];
                    bytes[i / 2] += FromHexNibble2Table[(byte) hexString[i + startIndex + 1]];
                }
            }

            return bytes;
        }
    }
}