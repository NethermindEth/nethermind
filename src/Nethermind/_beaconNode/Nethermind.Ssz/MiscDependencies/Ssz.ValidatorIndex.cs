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
using System.Runtime.InteropServices;
using Nethermind.Core2;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int ValidatorIndexLength = sizeof(ulong);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, ValidatorIndex value, ref int offset)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), value.Number);
            offset += Ssz.ValidatorIndexLength;
        }

        public static void Encode(Span<byte> span, ValidatorIndex value)
        {
            Encode(span, value.Number);
        }

        public static ValidatorIndex DecodeValidatorIndex(Span<byte> span)
        {
            return new ValidatorIndex(DecodeULong(span));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ValidatorIndex DecodeValidatorIndex(ReadOnlySpan<byte> span, ref int offset)
        {
            ValidatorIndex validatorIndex = new ValidatorIndex(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset)));
            offset += Ssz.ValidatorIndexLength;
            return validatorIndex;
        }

        public static void Encode(Span<byte> span, Span<ValidatorIndex> value)
        {
            if (span.Length != value.Length * Ssz.ValidatorIndexLength)
            {
                ThrowTargetLength<ulong[]>(span.Length, value.Length);
            }

            MemoryMarshal.Cast<ValidatorIndex, byte>(value).CopyTo(span);
        }
        
        private static void Encode(Span<byte> span, ValidatorIndex[]? containers, ref int offset, ref int dynamicOffset)
        {
            if (containers is null)
            {
                return;
            }
            
            int length = containers.Length * Ssz.ValidatorIndexLength;
            Encode(span, dynamicOffset, ref offset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
        }

        public static ValidatorIndex[] DecodeValidatorIndexes(ReadOnlySpan<byte> span)
        {
            return MemoryMarshal.Cast<byte, ValidatorIndex>(span).ToArray();
        }
    }
}