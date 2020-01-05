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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core2;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Hash32 value, ref int offset)
        {
            Encode(span.Slice(offset, ByteLength.Hash32Length), value);
            offset += ByteLength.Hash32Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, Hash32 value)
        {
            Encode(span, value.Bytes ?? Hash32.Zero.Bytes);
        }
        
//        public static void Encode(Span<byte> span, ReadOnlySpan<Hash32> value)
//        {
//            for (int i = 0; i < value.Length; i++)
//            {
//                Encode(span.Slice(i * ByteLength.Hash32Length, ByteLength.Hash32Length), value[i]);    
//            }
//        }

        public static void Encode(Span<byte> span, IReadOnlyList<Hash32> value)
        {
            for (int i = 0; i < value.Count; i++)
            {
                Encode(span.Slice(i * ByteLength.Hash32Length, ByteLength.Hash32Length), value[i]);    
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Hash32 DecodeSha256(Span<byte> span, ref int offset)
        {
            Hash32 hash32 = DecodeSha256(span.Slice(offset, ByteLength.Hash32Length));
            offset += ByteLength.Hash32Length;
            return hash32;
        }
        
        public static Hash32 DecodeSha256(Span<byte> span)
        {
            return Bytes.AreEqual(Hash32.Zero.Bytes, span) ? Hash32.Zero : new Hash32(DecodeBytes(span).ToArray());
        }
        
        public static Hash32[] DecodeHashes(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<Hash32>();
            }
            
            int count = span.Length / ByteLength.Hash32Length;
            Hash32[] result = new Hash32[count];
            for (int i = 0; i < count; i++)
            {
                Span<byte> current = span.Slice(i * ByteLength.Hash32Length, ByteLength.Hash32Length);
                result[i] = DecodeSha256(current);
            }

            return result;
        }
    }
}