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
using System.Diagnostics;

namespace Nethermind.EvmPlayground
{
    [DebuggerStepThrough]
    public static class Bytes
    {
        public static readonly byte[] Empty = new byte[0];

        private static readonly uint[] Lookup32 = CreateLookup32("x2");

        public static string ToHexString(this byte[] bytes)
        {
            return ToHexString(bytes, false, false);
        }
        
        public static string ToHexString(this Span<byte> bytes)
        {
            return ToHexString(bytes, false, false);
        }

        public static string ToHexString(this byte[] bytes, bool withZeroX)
        {
            return ToHexString(bytes, withZeroX, false);
        }
        
        public static string ToHexString(this Span<byte> bytes, bool withZeroX)
        {
            return ToHexString(bytes, withZeroX, false);
        }

        private static string ToHexString(byte[] bytes, bool withZeroX, bool skipLeadingZeros)
        {
            return ToHexString(bytes.AsSpan(), withZeroX, skipLeadingZeros);
        }

        private static string ToHexString(Span<byte> bytes, bool withZeroX, bool skipLeadingZeros)
        {
            int leadingZeros = skipLeadingZeros ? CountLeadingZeroNibbles(bytes) : 0;
            var result = new char[bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros];

            if (withZeroX)
            {
                result[0] = '0';
                result[1] = 'x';
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                uint val = Lookup32[bytes[i]];
                char char1 = (char) val;
                char char2 = (char) (val >> 16);

                if (leadingZeros <= i * 2) result[2 * i + (withZeroX ? 2 : 0) - leadingZeros] = char1;

                if (leadingZeros <= i * 2 + 1) result[2 * i + 1 + (withZeroX ? 2 : 0) - leadingZeros] = char2;
            }

            if (skipLeadingZeros && result.Length == (withZeroX ? 2 : 0)) return withZeroX ? "0x0" : "0";

            return new string(result);
        }

        private static uint[] CreateLookup32(string format)
        {
            var result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString(format);
                result[i] = s[0] + ((uint) s[1] << 16);
            }

            return result;
        }

        private static int CountLeadingZeroNibbles(Span<byte> bytes)
        {
            int leadingZeros = 0;
            for (int i = 0; i < bytes.Length; i++)
                if ((bytes[i] & 240) == 0)
                {
                    leadingZeros++;
                    if ((bytes[i] & 15) == 0)
                        leadingZeros++;
                    else
                        break;
                }
                else
                {
                    break;
                }

            return leadingZeros;
        }

        public static byte[] FromHexString(string hexString)
        {
            if (hexString == null) throw new ArgumentNullException($"{nameof(hexString)}");

            int startIndex = hexString.StartsWith("0x") ? 2 : 0;
            if (hexString.Length % 2 == 1) hexString = hexString.Insert(startIndex, "0");

            int numberChars = hexString.Length - startIndex;

            var bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2) bytes[i / 2] = Convert.ToByte(hexString.Substring(i + startIndex, 2), 16);

            return bytes;
        }
    }
}