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
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core2;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int GweiLength = sizeof(ulong);
        
        public static void Encode(Span<byte> span, Gwei value)
        {
            Encode(span, value.Amount);
        }
        
        public static void Encode(Span<byte> span, Gwei[]? value)
        {
            if (value is null)
            {
                return;
            }
            
            for (int i = 0; i < value.Length; i++)
            {
                Encode(span.Slice(i * Ssz.GweiLength, Ssz.GweiLength), value[i].Amount);    
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Gwei value, ref int offset)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), value.Amount);
            offset += Ssz.GweiLength;
        }
        
        public static Gwei DecodeGwei(Span<byte> span)
        {
            return new Gwei(DecodeULong(span));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Gwei DecodeGwei(ReadOnlySpan<byte> span, ref int offset)
        {
            Gwei gwei = new Gwei(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset)));
            offset += Ssz.GweiLength;
            return gwei;
        }
        
        public static Gwei[] DecodeGweis(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<Gwei>();
            }
            
            int count = span.Length / Ssz.GweiLength;
            Gwei[] result = new Gwei[count];
            for (int i = 0; i < count; i++)
            {
                Span<byte> current = span.Slice(i * Ssz.GweiLength, Ssz.GweiLength);
                result[i] = DecodeGwei(current);
            }

            return result;
        }
    }
}