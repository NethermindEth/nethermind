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
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int RootLength = Root.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Root value, ref int offset)
        {
            Encode(span.Slice(offset, Ssz.RootLength), value);
            offset += Ssz.RootLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, Root value)
        {
            Encode(span, value.AsSpan());
        }
        
//        public static void Encode(Span<byte> span, ReadOnlySpan<Hash32> value)
//        {
//            for (int i = 0; i < value.Length; i++)
//            {
//                Encode(span.Slice(i * Ssz.Hash32Length, Ssz.Hash32Length), value[i]);    
//            }
//        }

        public static void Encode(Span<byte> span, IReadOnlyList<Root> value)
        {
            for (int i = 0; i < value.Count; i++)
            {
                Encode(span.Slice(i * Ssz.RootLength, Ssz.RootLength), value[i]);    
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Root DecodeRoot(ReadOnlySpan<byte> span, ref int offset)
        {
            Root hash32 = DecodeRoot(span.Slice(offset, Ssz.RootLength));
            offset += Ssz.RootLength;
            return hash32;
        }
        
        public static Root DecodeRoot(ReadOnlySpan<byte> span)
        {
            return new Root(span);
        }
        
        public static Root[] DecodeRoots(ReadOnlySpan<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<Root>();
            }
            
            int count = span.Length / Ssz.RootLength;
            Root[] result = new Root[count];
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> current = span.Slice(i * Ssz.RootLength, Ssz.RootLength);
                result[i] = DecodeRoot(current);
            }

            return result;
        }
    }
}