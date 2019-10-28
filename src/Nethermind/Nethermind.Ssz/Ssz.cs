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

        public static void EncodeInt8(Span<byte> span, byte value)
        {
            span[0] = value;
        }

        public static void EncodeInt16(Span<byte> span, ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        }

        public static void EncodeInt32(Span<byte> span, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }

        public static void EncodeInt64(Span<byte> span, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        }

        public static void EncodeInt128(Span<byte> span, UInt128 value)
        {
            
        }

        public static void EncodeInt256(Span<byte> span, UInt256 value)
        {
            throw new NotImplementedException();
        }

        public static void EncodeBool(Span<byte> span, bool value)
        {
            span[0] = value ? (byte)1 : (byte)0;
        }
    }
}