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
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Extensions
{
    // TODO: move to ByteArrayExtensions and ByteExtensions
    public static class Bytes
    {
        public static readonly byte[] Empty = new byte[0]; // consider immutable 

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

        public static unsafe bool UnsafeCompare(byte[] a1, byte[] a2)
        {
            if (ReferenceEquals(a1, a2))
            {
                return true;
            }

            if (a1 == null || a2 == null || a1.Length != a2.Length)
            {
                return false;
            }

            fixed (byte* p1 = a1, p2 = a2)
            {
                byte* x1 = p1, x2 = p2;
                int l = a1.Length;
                for (int i = 0; i < l / 8; i++, x1 += 8, x2 += 8)
                {
                    if (*((long*)x1) != *((long*)x2))
                    {
                        return false;
                    }
                }

                if ((l & 4) != 0)
                {
                    if (*((int*)x1) != *((int*)x2))
                    {
                        return false;
                    }

                    x1 += 4;
                    x2 += 4;
                }

                if ((l & 2) != 0)
                {
                    if (*((short*)x1) != *((short*)x2))
                    {
                        return false;
                    }

                    x1 += 2;
                    x2 += 2;
                }

                if ((l & 1) != 0)
                {
                    if (*x1 != *x2)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public static bool IsZero(this byte[] bytes)
        {
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

        public static int LeadingZerosCount(this byte[] bytes, int startIndex = 0)
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

        public static byte[] WithoutLeadingZeros(this byte[] bytes)
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
            if (bytes.Length == length)
            {
                return (byte[])bytes.Clone();
            }

            if (bytes.Length > length)
            {
                return bytes.Slice(0, length);
            }

            byte[] result = new byte[length];
            Buffer.BlockCopy(bytes, 0, result, length - bytes.Length, bytes.Length);

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
                return (byte[])bytes.Clone();
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

        public static byte[] ReverseInPlace(this byte[] bytes)
        {
            Array.Reverse(bytes);
            return bytes;
        }

        // seems that Reverse and additional allocation makes it slower than iteration
        ////public static BigInteger ToUnsignedBigIntegerSlower(this byte[] bytes, Endianness endianness = Endianness.Big)
        ////{
        ////    byte[] unsignedResult = new byte[bytes.Length + 1];
        ////    Buffer.BlockCopy(bytes, 0, unsignedResult, 1, bytes.Length);
        ////    Array.Reverse(unsignedResult);

        ////    return new BigInteger(unsignedResult);
        ////}

        public static BigInteger ToUnsignedBigInteger(this Hex hex, Endianness endianness = Endianness.Big)
        {
            return ((byte[])hex).ToUnsignedBigInteger();
        }

        public static BigInteger ToUnsignedBigInteger(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big || !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                byte[] unsignedResult = new byte[bytes.Length + 1];
                for (int i = 0; i < bytes.Length; i++)
                {
                    unsignedResult[bytes.Length - i - 1] = bytes[i];
                }

                return new BigInteger(unsignedResult);
            }

            return new BigInteger(bytes);
        }

        /// <summary>
        /// Not tested, possibly broken
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="endianness"></param>
        /// <returns></returns>
        public static int ToInt32(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big || !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                byte[] reverted = new byte[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    reverted[bytes.Length - i - 1] = bytes[i];
                }

                return BitConverter.ToInt32(reverted.PadRight(4), 0);
            }

            return BitConverter.ToInt32(bytes.PadLeft(4), 0);
        }

        /// <summary>
        /// Not tested, possibly broken
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="endianness"></param>
        /// <returns></returns>
        public static uint ToUInt32(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big || !BitConverter.IsLittleEndian && endianness == Endianness.Little)
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

        public static BigInteger ToSignedBigInteger(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big || !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                byte[] signedResult = new byte[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    signedResult[bytes.Length - i - 1] = bytes[i];
                }

                return new BigInteger(signedResult);
            }

            return new BigInteger(bytes);
        }

        public static ulong ToUInt64(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big || !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                Array.Reverse(bytes);
            }

            bytes = PadRight(bytes, 8);
            ulong result = BitConverter.ToUInt64(bytes, 0);
            return result;
        }

        public static long ToInt64(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big || !BitConverter.IsLittleEndian && endianness == Endianness.Little)
            {
                Array.Reverse(bytes);
            }

            bytes = PadRight(bytes, 8);
            long result = BitConverter.ToInt64(bytes, 0);
            return result;
        }

        public static byte[] ToBytes(this BitArray bits)
        {
            if (bits.Length % 8 != 0)
            {
                throw new ArgumentException(nameof(bits));
            }

            byte[] bytes = new byte[bits.Length / 8];
            for (int i = 0; i < bits.Length; i++)
            {
                if (bits[i])
                {
                    bytes[i / 8] |= (byte)(1 << (7 - i % 8));
                }
            }

            return bytes;
        }

        public static void ToBigEndianBitArray256(this byte[] bytes, ref BitArray bitArray)
        {
            bitArray.SetAll(false);
            int startIndex = 256 - bytes.Length * 8;
            for (int i = startIndex; i < 256; i++)
            {
                bitArray[i] = bytes[(i - startIndex) / 8].GetBit(i % 8);
            }
        }

        public static void ToBigEndianBitArray2048(this byte[] bytes, ref BitArray bitArray)
        {
            bitArray.SetAll(false);
            int startIndex = 2048 - bytes.Length * 8;
            for (int i = startIndex; i < 2048; i++)
            {
                bitArray[i] = bytes[(i - startIndex) / 8].GetBit(i % 8);
            }
        }

        public static BitArray ToBigEndianBitArray256(this byte[] bytes)
        {
            BitArray bitArray = new BitArray(256);
            ToBigEndianBitArray256(bytes, ref bitArray);
            return bitArray;
        }

        public static BitArray ToBigEndianBitArray2048(this byte[] bytes)
        {
            BitArray bitArray = new BitArray(2048);
            ToBigEndianBitArray2048(bytes, ref bitArray);
            return bitArray;
        }
    }
}