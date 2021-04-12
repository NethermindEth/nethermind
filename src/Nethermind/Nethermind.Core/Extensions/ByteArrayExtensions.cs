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
using System.Numerics;

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
                return new[] {bytes[startIndex]};
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

            bytes.Slice(startIndex, copiedFragmentLength).CopyTo(slice.AsSpan().Slice(0, copiedFragmentLength));
            return slice;
        }
    }
}
