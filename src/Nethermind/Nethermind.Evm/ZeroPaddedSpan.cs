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

namespace Nethermind.Evm
{
    public ref struct ZeroPaddedSpan
    {
        public static ZeroPaddedSpan Empty => new(Span<byte>.Empty, 0, PadDirection.Right);
        
        public ZeroPaddedSpan(ReadOnlySpan<byte> span, int paddingLength, PadDirection padDirection)
        {
            PadDirection = padDirection;
            Span = span;
            PaddingLength = paddingLength;
        }

        public PadDirection PadDirection;
        public ReadOnlySpan<byte> Span;
        public int PaddingLength;
        public int Length => Span.Length + PaddingLength;

        /// <summary>
        /// Temporary to handle old invocations
        /// </summary>
        /// <returns></returns>
        public readonly byte[] ToArray()
        {
            byte[] result = new byte[Span.Length + PaddingLength];
            Span.CopyTo(result.AsSpan().Slice(PadDirection == PadDirection.Right ? 0 : PaddingLength, Span.Length));
            return result;
        }
    }
    
    public ref struct ZeroPaddedMemory
    {
        public static ZeroPaddedMemory Empty => new(Memory<byte>.Empty, 0, PadDirection.Right);

        public ZeroPaddedMemory(ReadOnlyMemory<byte> memory, int paddingLength, PadDirection padDirection)
        {
            PadDirection = padDirection;
            Memory = memory;
            PaddingLength = paddingLength;
        }

        public PadDirection PadDirection;
        public ReadOnlyMemory<byte> Memory;
        public int PaddingLength;
        public int Length => Memory.Length + PaddingLength;

        /// <summary>
        /// Temporary to handle old invocations
        /// </summary>
        /// <returns></returns>
        public readonly byte[] ToArray()
        {
            byte[] result = new byte[Memory.Length + PaddingLength];
            Memory.CopyTo(result.AsMemory().Slice(PadDirection == PadDirection.Right ? 0 : PaddingLength, Memory.Length));
            return result;
        }
    }
}
