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
using System.Data.HashFunction;
using System.Diagnostics;
using System.Numerics;

namespace Nevermind.Core.Extensions
{
    public static class ByteArrayExtensions
    {
        private static readonly xxHash XxHash = new xxHash(32);

        public static string ToHex(this byte[] bytes, bool withZeroX = true)
        {
            return Hex.FromBytes(bytes, withZeroX);
        }

        public static byte[] Xor(this byte[] bytes, byte[] otherBytes)
        {
            Debug.Assert(bytes.Length == otherBytes.Length, $"Expected same length when xoring but {nameof(bytes)}.Length == {bytes.Length} and {nameof(otherBytes)} == {otherBytes.Length}");
            byte[] result = new byte[bytes.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte)(bytes[i] ^ otherBytes[i]);
            }

            return result;
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

        public static byte[] SliceWithZeroPadding(this byte[] bytes, BigInteger startIndex, int length)
        {
            if (startIndex >= bytes.Length)
            {
                return new byte[length];
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

        public static int GetXxHashCode(this byte[] bytes)
        {
            return BitConverter.ToInt32(XxHash.ComputeHash(bytes), 0);
        }
    }
}