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
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, BitArray value, ref int offset)
        {
            int byteLength = (value.Length + 7) / 8;
            EncodeVector(span.Slice(offset, byteLength), value);
            offset += byteLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EncodeVector(Span<byte> span, BitArray value)
        {
            int byteLength = (value.Length + 7) / 8;
            byte[] bytes = new byte[byteLength];
            value.CopyTo(bytes, 0);
            Encode(span, bytes);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, BitArray value, ref int offset, ref int dynamicOffset)
        {
            int length = (value.Length + 8) / 8;
            Encode(span, dynamicOffset, ref offset);
            EncodeList(span.Slice(dynamicOffset, length), value);
            dynamicOffset += length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EncodeList(Span<byte> span, BitArray value)
        {
            int byteLength = (value.Length + 8) / 8;
            byte[] bytes = new byte[byteLength];
            value.CopyTo(bytes, 0);
            bytes[byteLength - 1] |= (byte)(1 << (value.Length % 8));
            Encode(span, bytes);
        }
        
        public static BitArray DecodeBitvector(ReadOnlySpan<byte> span, int vectorLength)
        {
            BitArray value = new BitArray(span.ToArray());
            value.Length = vectorLength;
            return value;
        }
        
        public static BitArray DecodeBitlist(ReadOnlySpan<byte> span)
        {
            BitArray value = new BitArray(span.ToArray());
            int length = value.Length - 1;
            int lastByte = span[span.Length - 1];
            int mask = 0x80;
            while ((lastByte & mask) == 0 && mask > 0)
            {
                length--;
                mask = mask >> 1;
            }
            value.Length = length;
            return value;
        }
    }
}