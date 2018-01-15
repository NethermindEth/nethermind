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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Core
{
    /// <summary>
    ///     https://stackoverflow.com/questions/311165/how-do-you-convert-a-byte-array-to-a-hexadecimal-string-and-vice-versa
    /// </summary>
    // from performance / memory perspective probably better to remove this class entirely, experimental
    public class Hex : IEquatable<Hex>
    {
        private static readonly uint[] Lookup32 = CreateLookup32("x2");
        private byte[] _bytes;
        private string _hexString;

        public Hex(string hexString)
        {
            _hexString = hexString.StartsWith("0x") ? hexString.Substring(2) : hexString;
        }

        public Hex(byte[] bytes)
        {
            _bytes = bytes;
        }

        public int ByteLength => _bytes?.Length ?? _hexString.Length / 2;
        public int StringLenght => _hexString?.Length ?? _bytes.Length * 2;

        public bool Equals(Hex obj)
        {
            if (obj == null)
            {
                return false;
            }

            if (_bytes != null && obj._bytes != null)
            {
                return Bytes.UnsafeCompare(_bytes, obj._bytes);
            }

            if (_hexString != null && obj._hexString != null)
            {
                return _hexString == obj;
            }

            if (_hexString != null && obj._hexString == null)
            {
                return _hexString == obj;
            }

            if (_hexString == null && obj._hexString != null)
            {
                return this == obj._hexString;
            }

            Debug.Assert(false, "one of the conditions should be true");
            return false;
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToString(bool withZeroX, bool noLeadingZeros = false)
        {
            if (_hexString == null)
            {
                _hexString = FromBytes(_bytes, false, false);
            }

            // this actually depends on whether it is quantity or byte data...
            string trimmed = noLeadingZeros ? _hexString.TrimStart('0') : _hexString;
            if (trimmed.Length == 0)
            {
                trimmed = string.Concat(trimmed, '0');
            }

            return withZeroX ? string.Concat("0x", trimmed) : trimmed;
        }

        public static implicit operator byte[] (Hex hex)
        {
            return hex._bytes ?? (hex._bytes = ToBytes(hex._hexString));
        }

        public static implicit operator string(Hex hex)
        {
            return hex.ToString(false);
        }

        public static implicit operator Hex(string hex)
        {
            if (hex == null)
            {
                return null;
            }

            return new Hex(hex);
        }

        public static implicit operator Hex(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            return new Hex(bytes);
        }

        public override bool Equals(object obj)
        {
            if (!(obj is Hex))
            {
                return false;
            }

            return Equals((Hex)obj);
        }

        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public override int GetHashCode()
        {
            if (_bytes == null)
            {
                _bytes = ToBytes(_hexString);
            }

            return _bytes.GetXxHashCode();
        }

        private static uint[] CreateLookup32(string format)
        {
            uint[] result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString(format);
                result[i] = s[0] + ((uint)s[1] << 16);
            }
            return result;
        }

        [DebuggerStepThrough]
        private static string ByteArrayToHexViaLookup32(byte[] bytes, bool withZeroX, bool skipLeadingZeros, bool withEip55Checksum)
        {
            int leadingZeros = skipLeadingZeros ? CountLeadingZeros(bytes) : 0;
            char[] result = new char[bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros];
            string hashHex = null;
            if (withEip55Checksum)
            {
                // I guess it may be better (faster) than calling ToString here
                hashHex = Keccak.Compute(new Hex(bytes).ToString(false)).ToString(false);
            }

            if (withZeroX)
            {
                result[0] = '0';
                result[1] = 'x';
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                uint val = Lookup32[bytes[i]];
                char char1 = (char)val;
                char char2 = (char)(val >> 16);

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

        private static int CountLeadingZeros(byte[] bytes)
        {
            int leadingZeros = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if ((bytes[i] & 240) == 0)
                {
                    leadingZeros++;
                    if ((bytes[i] & 15) == 0)
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

        public static string FromBytes(byte[] bytes, bool withZeroX)
        {
            return FromBytes(bytes, withZeroX, false, false);
        }

        public static string FromBytes(byte[] bytes, bool withZeroX, bool noLeadingZeros)
        {
            return FromBytes(bytes, withZeroX, noLeadingZeros, false);
        }

        public static string FromBytes(byte[] bytes, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ByteArrayToHexViaLookup32(bytes, withZeroX, noLeadingZeros, withEip55Checksum);
        }

        public static Nibble[] ToNibbles(string hexString)
        {
            if (hexString == null)
            {
                throw new ArgumentNullException($"{nameof(hexString)}");
            }

            int startIndex = hexString.StartsWith("0x") ? 2 : 0;
            int numberChars = hexString.Length - startIndex;

            Nibble[] nibbles = new Nibble[numberChars];
            for (int i = 0; i < numberChars; i++)
            {
                nibbles[i] = new Nibble(hexString[i + startIndex]);
            }

            return nibbles;
        }

        public static byte[] ToBytes(string hexString)
        {
            if (hexString == null)
            {
                throw new ArgumentNullException($"{nameof(hexString)}");
            }

            int startIndex = hexString.StartsWith("0x") ? 2 : 0;
            if (hexString.Length % 2 == 1)
            {
                hexString = hexString.Insert(startIndex, "0");
            }

            int numberChars = hexString.Length - startIndex;

            byte[] bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hexString.Substring(i + startIndex, 2), 16);
            }

            return bytes;
        }
    }
}