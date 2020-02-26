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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm
{
    public static class ByteArrayExtensions
    {
        private static ZeroPaddedSpan SliceWithZeroPadding(this Span<byte> bytes, int startIndex, int length)
        {
            if (startIndex >= bytes.Length)
            {
                return new ZeroPaddedSpan(Span<byte>.Empty, length);
            }

            if (length == 1)
            {
                // why do we return zero length here?
                // it was passing all the tests like this...
                // return bytes.Length == 0 ? new byte[0] : new[] {bytes[startIndex]};
                return bytes.Length == 0 ? new ZeroPaddedSpan(Span<byte>.Empty, 0) : new ZeroPaddedSpan(bytes.Slice(startIndex, 1), 0);
                // return bytes.Length == 0 ? new ZeroPaddedSpan(Span<byte>.Empty, 1) : new ZeroPaddedSpan(bytes.Slice(startIndex, 1), 0);
            }
            
            int copiedLength = Math.Min(bytes.Length - startIndex, length);
            return new ZeroPaddedSpan(bytes.Slice(startIndex, copiedLength), length - copiedLength);
        }

        public static ZeroPaddedSpan SliceWithZeroPadding(this Span<byte> bytes, UInt256 startIndex, int length)
        {
            if (startIndex >= bytes.Length || startIndex > int.MaxValue)
            {
                return new ZeroPaddedSpan(Span<byte>.Empty, length);
            }

            return SliceWithZeroPadding(bytes, (int) startIndex, length);
        }
        
        public static ZeroPaddedSpan SliceWithZeroPadding(this byte[] bytes, UInt256 startIndex, int length)
        {
            return bytes.AsSpan().SliceWithZeroPadding(startIndex, length);
        }
        
        public static ZeroPaddedSpan SliceWithZeroPadding(this byte[] bytes, int startIndex, int length)
        {
            return bytes.AsSpan().SliceWithZeroPadding(startIndex, length);
        }
    }
}