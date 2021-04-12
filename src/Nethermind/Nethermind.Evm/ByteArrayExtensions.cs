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
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class ByteArrayExtensions
    {
        private static ZeroPaddedSpan SliceWithZeroPadding(this Span<byte> span, int startIndex, int length, PadDirection padDirection)
        {
            if (startIndex >= span.Length)
            {
                return new ZeroPaddedSpan(Span<byte>.Empty, length, padDirection);
            }

            if (length == 1)
            {
                // why do we return zero length here?
                // it was passing all the tests like this...
                // return bytes.Length == 0 ? new byte[0] : new[] {bytes[startIndex]};
                return span.Length == 0 ? new ZeroPaddedSpan(Span<byte>.Empty, 0, padDirection) : new ZeroPaddedSpan(span.Slice(startIndex, 1), 0, padDirection);
                // return bytes.Length == 0 ? new ZeroPaddedSpan(Span<byte>.Empty, 1) : new ZeroPaddedSpan(bytes.Slice(startIndex, 1), 0);
            }
            
            int copiedLength = Math.Min(span.Length - startIndex, length);
            return new ZeroPaddedSpan(span.Slice(startIndex, copiedLength), length - copiedLength, padDirection);
        }
        
        private static ZeroPaddedMemory MemoryWithZeroPadding(this ReadOnlyMemory<byte> memory, int startIndex, int length, PadDirection padDirection)
        {
            if (startIndex >= memory.Length)
            {
                return new ZeroPaddedMemory(ReadOnlyMemory<byte>.Empty, length, padDirection);
            }

            if (length == 1)
            {
                // why do we return zero length here?
                // it was passing all the tests like this...
                // return bytes.Length == 0 ? new byte[0] : new[] {bytes[startIndex]};
                return memory.Length == 0 ? new ZeroPaddedMemory(ReadOnlyMemory<byte>.Empty, 0, padDirection) : new ZeroPaddedMemory(memory.Slice(startIndex, 1), 0, padDirection);
                // return bytes.Length == 0 ? new ZeroPaddedSpan(Span<byte>.Empty, 1) : new ZeroPaddedSpan(bytes.Slice(startIndex, 1), 0);
            }
            
            int copiedLength = Math.Min(memory.Length - startIndex, length);
            return new ZeroPaddedMemory(memory.Slice(startIndex, copiedLength), length - copiedLength, padDirection);
        }

        public static ZeroPaddedSpan SliceWithZeroPadding(this Span<byte> span, UInt256 startIndex, int length, PadDirection padDirection = PadDirection.Right)
        {
            if (startIndex >= span.Length || startIndex > int.MaxValue)
            {
                return new ZeroPaddedSpan(Span<byte>.Empty, length, PadDirection.Right);
            }

            return SliceWithZeroPadding(span, (int) startIndex, length, padDirection);
        }
        
        public static ZeroPaddedMemory SliceWithZeroPadding(this ReadOnlyMemory<byte> bytes, UInt256 startIndex, int length, PadDirection padDirection = PadDirection.Right)
        {
            if (startIndex >= bytes.Length || startIndex > int.MaxValue)
            {
                return new ZeroPaddedMemory(ReadOnlyMemory<byte>.Empty, length, PadDirection.Right);
            }

            return MemoryWithZeroPadding(bytes, (int) startIndex, length, padDirection);
        }
        
        public static ZeroPaddedSpan SliceWithZeroPadding(this byte[] bytes, UInt256 startIndex, int length, PadDirection padDirection = PadDirection.Right) => 
            bytes.AsSpan().SliceWithZeroPadding(startIndex, length, padDirection);

        public static ZeroPaddedSpan SliceWithZeroPadding(this byte[] bytes, int startIndex, int length, PadDirection padDirection = PadDirection.Right) => 
            bytes.AsSpan().SliceWithZeroPadding(startIndex, length, padDirection);
    }
}
