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
using System.Diagnostics;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Extensions
{
    public static class SpanExtensions
    {
        public static string ToHexString(this in Span<byte> span, bool withZeroX)
        {
            return ToHexString(span, withZeroX, false, false);
        }
        
        public static string ToHexString(this in Span<byte> span)
        {
            return ToHexString(span, false, false, false);
        }
        
        public static string ToHexString(this in Span<byte> span, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ToHexViaLookup(span, withZeroX, noLeadingZeros, withEip55Checksum);
        }
        
        [DebuggerStepThrough]
        private static string ToHexViaLookup(in Span<byte> span, bool withZeroX, bool skipLeadingZeros, bool withEip55Checksum)
        {
            int leadingZeros = skipLeadingZeros ? CountLeadingZeros(span) : 0;
            char[] result = new char[span.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros];
            string? hashHex = null;
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
                        withEip55Checksum && char.IsLetter(char1) && hashHex![2 * i] > '7'
                            ? char.ToUpper(char1)
                            : char1;
                }

                if (leadingZeros <= i * 2 + 1)
                {
                    result[2 * i + 1 + (withZeroX ? 2 : 0) - leadingZeros] =
                        withEip55Checksum && char.IsLetter(char2) && hashHex![2 * i + 1] > '7'
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
        
        private static int CountLeadingZeros(in Span<byte> span)
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

        public static bool IsNullOrEmpty<T>(this in Span<T> span) => span == null || span.Length == 0;
    }
}
