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
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int Bytes32Length = Bytes32.Length;

        public static ReadOnlySpan<byte> DecodeBytes(ReadOnlySpan<byte> span)
        {
            return span.ToArray();
        }
        
        public static Bytes32 DecodeBytes32(ReadOnlySpan<byte> span)
        {
            return new Bytes32(span);
        }

        public static Bytes32[] DecodeBytes32s(ReadOnlySpan<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<Bytes32>();
            }

            int count = span.Length / Bytes32Length;
            Bytes32[] result = new Bytes32[count];
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> current = span.Slice(i * Bytes32Length, Bytes32Length);
                result[i] = DecodeBytes32(current);
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, Bytes32 value)
        {
            Encode(span, value.AsSpan());
        }

        public static void Encode(Span<byte> span, IReadOnlyList<Bytes32> value)
        {
            for (int i = 0; i < value.Count; i++)
            {
                Encode(span.Slice(i * Bytes32Length, Bytes32Length), value[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Bytes32 DecodeBytes32(ReadOnlySpan<byte> span, ref int offset)
        {
            Bytes32 bytes32 = DecodeBytes32(span.Slice(offset, Bytes32Length));
            offset += Bytes32Length;
            return bytes32;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Bytes32 value, ref int offset)
        {
            Encode(span.Slice(offset, Bytes32Length), value);
            offset += Bytes32Length;
        }
    }
}