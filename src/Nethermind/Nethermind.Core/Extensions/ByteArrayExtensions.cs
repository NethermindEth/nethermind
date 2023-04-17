// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Extensions
{
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
                return new[] { bytes[startIndex] };
            }

            byte[] slice = new byte[length];
            Buffer.BlockCopy(bytes, startIndex, slice, 0, length);
            return slice;
        }

        public static byte[] SliceWithZeroPaddingEmptyOnError(this byte[] bytes, int startIndex, int length)
        {
            int copiedFragmentLength = Math.Min(bytes.Length - startIndex, length);
            if (copiedFragmentLength <= 0)
            {
                return Array.Empty<byte>();
            }

            byte[] slice = new byte[length];

            Buffer.BlockCopy(bytes, startIndex, slice, 0, copiedFragmentLength);
            return slice;
        }

        public static byte[] SliceWithZeroPaddingEmptyOnError(this ReadOnlySpan<byte> bytes, int startIndex, int length)
        {
            int copiedFragmentLength = Math.Min(bytes.Length - startIndex, length);
            if (copiedFragmentLength <= 0)
            {
                return Array.Empty<byte>();
            }

            byte[] slice = new byte[length];

            bytes.Slice(startIndex, copiedFragmentLength).CopyTo(slice.AsSpan(0, copiedFragmentLength));
            return slice;
        }
    }
}
