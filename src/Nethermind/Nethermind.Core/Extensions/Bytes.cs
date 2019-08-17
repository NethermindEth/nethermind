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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Extensions
{
    public static class Bytes
    {
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

        public static readonly byte[] Zero32 = new byte[32];

        public static readonly byte[] Zero256 = new byte[256];

        public static readonly byte[] Empty = new byte[0];

        public enum Endianness
        {
            Big,
            Little
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool GetBit(this byte b, int bitNumber)
        {
            return (b & (1 << (7 - bitNumber))) != 0;
        }

        public static int GetHighestSetBitIndex(this byte b)
        {
            if ((b & 128) == 128) return 7;
            if ((b & 64) == 64) return 6;
            if ((b & 32) == 32) return 5;
            if ((b & 16) == 16) return 4;
            if ((b & 8) == 8) return 3;
            if ((b & 4) == 4) return 2;
            return (b & 2) == 2 ? 1 : 0;
        }

        public static bool AreEqual(Span<byte> a1, Span<byte> a2)
        {
            return a1.SequenceEqual(a2);
        }

        public static bool IsZero(this byte[] bytes)
        {
            if (bytes.Length == 32)
            {
                return bytes[0] == 0 && bytes.AsSpan().SequenceEqual(Bytes.Zero32);
            }

            for (int i = 0; i < bytes.Length / 2; i++)
            {
                if (bytes[i] != 0)
                {
                    return false;
                }

                if (bytes[bytes.Length - i - 1] != 0)
                {
                    return false;
                }
            }

            return bytes.Length % 2 == 0 || bytes[bytes.Length / 2] == 0;
        }

        public static int LeadingZerosCount(this Span<byte> bytes, int startIndex = 0)
        {
            for (int i = startIndex; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    return i - startIndex;
                }
            }

            return bytes.Length - startIndex;
        }

        public static int TrailingZerosCount(this byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[bytes.Length - i - 1] != 0)
                {
                    return i;
                }
            }

            return bytes.Length;
        }

        public static Span<byte> WithoutLeadingZeros(this byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    return bytes.AsSpan().Slice(i, bytes.Length - i);
                }
            }

            return new byte[] {0};
        }

        public static Span<byte> WithoutLeadingZeros(this Span<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    return bytes.Slice(i, bytes.Length - i);
                }
            }

            return new byte[] {0};
        }

        public static byte[] Concat(byte prefix, byte[] bytes)
        {
            byte[] result = new byte[1 + bytes.Length];
            result[0] = prefix;
            Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length);
            return result;
        }

        public static byte[] Concat(byte prefix, byte[] part1, byte[] part2)
        {
            byte[] output = new byte[1 + part1.Length + part2.Length];
            output[0] = prefix;
            Buffer.BlockCopy(part1, 0, output, 1, part1.Length);
            Buffer.BlockCopy(part2, 0, output, 1 + part1.Length, part2.Length);
            return output;
        }

        public static byte[] PadLeft(this byte[] bytes, int length, byte padding = 0)
        {
            return PadLeft(bytes.AsSpan(), length, padding);
        }

        public static byte[] PadLeft(this Span<byte> bytes, int length, byte padding = 0)
        {
            if (bytes.Length == length)
            {
                return bytes.ToArray();
            }

            if (bytes.Length > length)
            {
                return bytes.Slice(0, length).ToArray();
            }

            byte[] result = new byte[length];
            bytes.CopyTo(result.AsSpan().Slice(length - bytes.Length));

            if (padding != 0)
            {
                for (int i = 0; i < length - bytes.Length; i++)
                {
                    result[i] = padding;
                }
            }

            return result;
        }

        public static byte[] PadRight(this byte[] bytes, int length)
        {
            if (bytes.Length == length)
            {
                return (byte[]) bytes.Clone();
            }

            if (bytes.Length > length)
            {
                return bytes.Slice(0, length);
            }

            byte[] result = new byte[length];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        public static byte[] Concat(params byte[][] parts)
        {
            int totalLength = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                totalLength += parts[i].Length;
            }

            byte[] result = new byte[totalLength];
            int position = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                Buffer.BlockCopy(parts[i], 0, result, position, parts[i].Length);
                position += parts[i].Length;
            }

            return result;
        }

        public static byte[] Concat(byte[] bytes, byte suffix)
        {
            byte[] result = new byte[bytes.Length + 1];
            result[result.Length - 1] = suffix;
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        public static byte[] Reverse(byte[] bytes)
        {
            byte[] result = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                result[i] = bytes[bytes.Length - i - 1];
            }

            return result;
        }

        public static BigInteger ToUnsignedBigInteger(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            return ToUnsignedBigInteger(bytes.AsSpan(), endianness);
        }

        public static BigInteger ToUnsignedBigInteger(this Span<byte> bytes, Endianness endianness = Endianness.Big)
        {
            return new BigInteger(bytes, true, endianness == Endianness.Big);
        }

        /// <summary>
        /// Fix, so no allocations are made
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="endianness"></param>
        /// <returns></returns>
        public static int ToInt32(this Span<byte> bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big ||
                !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                byte[] reverted = new byte[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    reverted[bytes.Length - i - 1] = bytes[i];
                }

                return BitConverter.ToInt32(reverted.PadRight(4), 0);
            }

            return BitConverter.ToInt32(bytes.ToArray().PadLeft(4), 0);
        }

        /// <summary>
        /// Not tested, possibly broken
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="endianness"></param>
        /// <returns></returns>
        public static uint ToUInt32(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big ||
                !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                byte[] reverted = new byte[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    reverted[bytes.Length - i - 1] = bytes[i];
                }

                return BitConverter.ToUInt32(reverted.PadRight(4), 0);
            }

            return BitConverter.ToUInt32(bytes.Length == 4 ? bytes : bytes.PadLeft(4), 0);
        }

        public static BigInteger ToSignedBigInteger(this byte[] bytes, int byteLength,
            Endianness endianness = Endianness.Big)
        {
            if (bytes.Length == byteLength)
            {
                return new BigInteger(bytes.AsSpan(), false, endianness == Endianness.Big);
            }

            Debug.Assert(bytes.Length <= byteLength,
                $"{nameof(ToSignedBigInteger)} expects {nameof(byteLength)} parameter to be less than length of the {bytes}");
            bool needToExpand = bytes.Length != byteLength;
            byte[] bytesToUse = needToExpand ? new byte[byteLength] : bytes;
            if (needToExpand)
            {
                Buffer.BlockCopy(bytes, 0, bytesToUse, byteLength - bytes.Length, bytes.Length);
            }

            if (BitConverter.IsLittleEndian && endianness == Endianness.Big ||
                !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                byte[] signedResult = new byte[byteLength];
                for (int i = 0; i < byteLength; i++)
                {
                    signedResult[byteLength - i - 1] = bytesToUse[i];
                }

                return new BigInteger(signedResult);
            }

            return new BigInteger(bytesToUse);
        }

        public static BigInteger ToSignedBigInteger(this Span<byte> bytes, int byteLength,
            Endianness endianness = Endianness.Big)
        {
            if (bytes.Length == byteLength)
            {
                return new BigInteger(bytes, false, endianness == Endianness.Big);
            }

            // Debug.Assert(bytes.Length <= byteLength, $"{nameof(Bytes.ToSignedBigInteger)} expects {nameof(byteLength)} parameter to be less than length of the {bytes}");
            Span<byte> bytesToUse = new byte[byteLength].AsSpan();
            bytes.CopyTo(bytesToUse.Slice(bytesToUse.Length - bytes.Length, bytes.Length));

            if (BitConverter.IsLittleEndian && endianness == Endianness.Big ||
                !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                bytesToUse.Reverse();
                //byte[] signedResult = new byte[byteLength];
                //for (int i = 0; i < byteLength; i++)
                //{
                //    signedResult[byteLength - i - 1] = bytesToUse[i];
                //}

                return new BigInteger(bytesToUse);
            }

            return new BigInteger(bytesToUse);
        }

        public static UInt256 ToUInt256(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (endianness == Endianness.Little)
            {
                throw new NotImplementedException();
            }

            UInt256.CreateFromBigEndian(out UInt256 result, bytes);

            return result;
        }

        public static ulong ToUInt64(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big ||
                !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                Array.Reverse(bytes);
            }

            bytes = PadRight(bytes, 8);
            ulong result = BitConverter.ToUInt64(bytes, 0);
            return result;
        }

        public static long ToInt64(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big ||
                !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                Array.Reverse(bytes);
            }

            bytes = PadRight(bytes, 8);
            long result = BitConverter.ToInt64(bytes, 0);
            return result;
        }

        private static byte Reverse(byte b)
        {
            b = (byte) ((b & 0xF0) >> 4 | (b & 0x0F) << 4);
            b = (byte) ((b & 0xCC) >> 2 | (b & 0x33) << 2);
            b = (byte) ((b & 0xAA) >> 1 | (b & 0x55) << 1);
            return b;
        }

        public static byte[] ToBytes(this BitArray bits)
        {
            if (bits.Length % 8 != 0)
            {
                throw new ArgumentException(nameof(bits));
            }

            byte[] bytes = new byte[bits.Length / 8];
            bits.CopyTo(bytes, 0);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Reverse(bytes[i]);
            }

            return bytes;
        }

        public static string ToBitString(this BitArray bits)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < bits.Count; i++)
            {
                char c = bits[i] ? '1' : '0';
                sb.Append(c);
            }

            return sb.ToString();
        }

        public static BitArray ToBigEndianBitArray256(this Span<byte> bytes)
        {
            byte[] inverted = new byte[32];
            int startIndex = 32 - bytes.Length;
            for (int i = startIndex; i < inverted.Length; i++)
            {
                inverted[i] = Reverse(bytes[i - startIndex]);
            }

            return new BitArray(inverted);
        }

        public static BitArray ToBigEndianBitArray2048(this byte[] bytes)
        {
            byte[] inverted = new byte[256];
            int startIndex = 256 - bytes.Length;
            for (int i = startIndex; i < inverted.Length; i++)
            {
                inverted[i] = Reverse(bytes[i - startIndex]);
            }

            return new BitArray(inverted);
        }

        public static BitArray ToBigEndianBitArray2048(this Span<byte> bytes)
        {
            byte[] inverted = new byte[256];
            int startIndex = 256 - bytes.Length;
            for (int i = startIndex; i < inverted.Length; i++)
            {
                inverted[i] = Reverse(bytes[i - startIndex]);
            }

            return new BitArray(inverted);
        }

        public static string ToHexString(this byte[] bytes)
        {
            return ToHexString(bytes, false, false, false);
        }

        public static void StreamHex(this byte[] bytes, StreamWriter streamWriter)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                uint val = Lookup32[bytes[i]];
                streamWriter.Write((char) val);
                streamWriter.Write((char) (val >> 16));
            }
        }

        public static string ToHexString(this byte[] bytes, bool withZeroX)
        {
            return ToHexString(bytes, withZeroX, false, false);
        }

        public static string ToHexString(this byte[] bytes, bool withZeroX, bool noLeadingZeros)
        {
            return ToHexString(bytes, withZeroX, noLeadingZeros, false);
        }

        public static string ToHexString(this byte[] bytes, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ByteArrayToHexViaLookup32(bytes, withZeroX, noLeadingZeros, withEip55Checksum);
        }

        [DebuggerStepThrough]
        private static string ByteArrayToHexViaLookup32(byte[] bytes, bool withZeroX, bool skipLeadingZeros,
            bool withEip55Checksum)
        {
            int leadingZeros = skipLeadingZeros ? CountLeadingZeros(bytes) : 0;
            char[] result = new char[bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros];
            string hashHex = null;
            if (withEip55Checksum)
            {
                hashHex = Keccak.Compute(bytes.ToHexString(false)).ToString(false);
            }

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

        public static byte[] FromHexString(string hexString)
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