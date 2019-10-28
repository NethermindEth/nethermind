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
using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Ssz
{
    /// <summary>
    /// https://github.com/ethereum/eth2.0-specs/blob/dev/specs/simple-serialize.md#simpleserialize-ssz
    /// </summary>
    public class Ssz
    {
        public const int BytesPerChunk = 32;
        public const int BytesPerLengthOffset = 4;
        public const int BitsPerByte = 8; // I guess I can remove this later...

        public static void Encode(Span<byte> span, BitArray bitArray)
        {
            byte[] bytes = bitArray.ToBytes();
        }

        public static void Encode(Span<byte> span, byte value)
        {
            span[0] = value;
        }

        public static void Encode(Span<byte> span, ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        }

        public static void Encode(Span<byte> span, int value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)value);
        }
        
        public static void Encode(Span<byte> span, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }

        public static void Encode(Span<byte> span, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        }

        public static void Encode(Span<byte> span, UInt128 value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(0, 8), value.S0);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8, 8), value.S1);
        }

        public static void Encode(Span<byte> span, UInt256 value)
        {
            value.ToLittleEndian(span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Encode(bool value)
        {
            return value ? (byte) 1 : (byte) 0;
        }

        public static void Encode(Span<byte> span, bool[] value)
        {
            if (span.Length != value.Length)
            {
                ThrowInvalidTargetLength(span.Length, value.Length);
            }

            for (int i = 0; i < value.Length; i++)
            {
                span[i] = Encode(value[i]);
            }
        }

        public static void Encode(Span<byte> span, UInt256[] value)
        {
            const int typeSize = 32;
            if (span.Length != value.Length * typeSize)
            {
                ThrowInvalidTargetLength(span.Length, value.Length);
            }

            for (int i = 0; i < value.Length; i++)
            {
                Encode(span.Slice(i * typeSize, typeSize), value[i]);
            }
        }

        public static void Encode(Span<byte> span, UInt128[] value)
        {
            const int typeSize = 16;
            if (span.Length != value.Length * typeSize)
            {
                ThrowInvalidTargetLength(span.Length, value.Length);
            }

            for (int i = 0; i < value.Length; i++)
            {
                Encode(span.Slice(i * typeSize, typeSize), value[i]);
            }
        }

        public static void Encode(Span<byte> span, ulong[] value)
        {
            const int typeSize = 8;
            if (span.Length != value.Length * typeSize)
            {
                ThrowInvalidTargetLength(span.Length, value.Length);
            }

            MemoryMarshal.Cast<ulong, byte>(value).CopyTo(span);
        }

        public static void Encode(Span<byte> span, uint[] value)
        {
            const int typeSize = 4;
            if (span.Length != value.Length * typeSize)
            {
                ThrowInvalidTargetLength(span.Length, value.Length);
            }

            MemoryMarshal.Cast<uint, byte>(value).CopyTo(span);
        }

        public static void Encode(Span<byte> span, ushort[] value)
        {
            const int typeSize = 2;
            if (span.Length != value.Length * typeSize)
            {
                ThrowInvalidTargetLength(span.Length, value.Length);
            }

            MemoryMarshal.Cast<ushort, byte>(value).CopyTo(span);
        }

        public static void Encode(Span<byte> span, byte[] value)
        {
            const int typeSize = 1;
            if (span.Length != value.Length * typeSize)
            {
                ThrowInvalidTargetLength(span.Length, value.Length);
            }

            value.AsSpan().CopyTo(span);
        }

        private static void ThrowInvalidTargetLength(int targetLength, int expectedLength)
        {
            throw new InvalidDataException($"Invalid target length in SSZ encoding. Target length is {targetLength} and expected length is {expectedLength}.");
        }
    }
}