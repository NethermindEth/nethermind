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
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Sha256 value, ref int offset)
        {
            Encode(span.Slice(offset, Sha256.SszLength), value);
            offset += Sha256.SszLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, Sha256 value)
        {
            Encode(span, value?.Bytes ?? Sha256.Zero.Bytes);
        }
        
        public static void Encode(Span<byte> span, Span<Sha256> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                Encode(span.Slice(i * Sha256.SszLength, Sha256.SszLength), value[i]);    
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Sha256 DecodeSha256(Span<byte> span, ref int offset)
        {
            Sha256 sha256 = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            return sha256;
        }
        
        public static Sha256 DecodeSha256(Span<byte> span)
        {
            return Bytes.AreEqual(Bytes.Zero32, span) ? null : new Sha256(DecodeBytes(span).ToArray());
        }
        
        public static Sha256[] DecodeHashes(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<Sha256>();
            }
            
            int count = span.Length / Sha256.SszLength;
            Sha256[] result = new Sha256[count];
            for (int i = 0; i < count; i++)
            {
                Span<byte> current = span.Slice(i * Sha256.SszLength, Sha256.SszLength);
                result[i] = DecodeSha256(current);
            }

            return result;
        }
    }
}