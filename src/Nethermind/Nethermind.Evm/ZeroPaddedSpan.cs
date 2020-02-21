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

namespace Nethermind.Evm
{
    public ref struct ZeroPaddedSpan
    {
        public static ZeroPaddedSpan Empty => new ZeroPaddedSpan(Span<byte>.Empty, 0);
        
        public ZeroPaddedSpan(Span<byte> span, int paddingLength)
        {
            Span = span;
            PaddingLength = paddingLength;
        }
        
        public Span<byte> Span;
        public int PaddingLength;
        public int Length => Span.Length + PaddingLength;

        /// <summary>
        /// Temporary to handle old invocations
        /// </summary>
        /// <returns></returns>
        public readonly byte[] ToArray()
        {
            byte[] result = new byte[Span.Length + PaddingLength];
            Span.CopyTo(result.AsSpan().Slice(0, Span.Length));
            return result;
        }
    }
}