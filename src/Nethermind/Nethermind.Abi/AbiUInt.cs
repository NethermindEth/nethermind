//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Abi
{
    public class AbiUInt : AbiType
    {
        private const int MaxSize = 256;

        private const int MinSize = 0;

        public AbiUInt(int length)
        {
            if (length % 8 != 0)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiUInt)} has to be a multiple of 8");
            }

            if (length > MaxSize)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiUInt)} has to be less or equal to {MaxSize}");
            }

            if (length <= MinSize)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiUInt)} has to be greater than {MinSize}");
            }

            Length = length;
            CSharpType = GetCSharpType();
        }

        public int Length { get; }

        public int LengthInBytes => Length / 8;

        public override string Name => $"uint{Length}";

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            var (value, length) = DecodeUInt(data, position, packed);
            
            switch (Length)
            {
                case { } n when n <= 8:
                    return ((byte) value, length);
                case { } n when n <= 16:
                    return ((ushort) value, length);
                case { } n when n <= 32:
                    return ((uint) value, length);
                case { } n when n <= 64:
                    return ((ulong) value, length);
                default:
                    return (value, length);
            }
        }

        public (UInt256, int) DecodeUInt(byte[] data, int position, bool packed)
        {
            UInt256 lengthData = new(data.Slice(position, (packed ? LengthInBytes : UInt256.LengthInBytes)), true);
            return (lengthData, position + (packed ? LengthInBytes : UInt256.LengthInBytes));
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            Span<byte> bytes = null;
            if (arg is UInt256 uint256)
            {
                bytes = ((BigInteger) uint256).ToBigEndianByteArray();
            }
            else if (arg is BigInteger bigInteger)
            {
                bytes = bigInteger.ToBigEndianByteArray();
            }
            else if (arg is int intInput)
            {
                bytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bytes, intInput);
            }
            else if (arg is uint uintInput)
            {
                bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, uintInput);
            }
            else if (arg is long longInput)
            {
                bytes = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(bytes, longInput);
            }
            else if (arg is ulong ulongInput)
            {
                bytes = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(bytes, ulongInput);
            }
            else if (arg is short shortInput)
            {
                bytes = new byte[8];
                BinaryPrimitives.WriteInt16BigEndian(bytes, shortInput);
            }
            else if (arg is ushort ushortInput)
            {
                bytes = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(bytes, ushortInput);
            }
            else
            {
                throw new AbiException(AbiEncodingExceptionMessage);
            }

            return bytes.PadLeft(packed ? LengthInBytes : UInt256.LengthInBytes);
        }

        public override Type CSharpType { get; }
        
        private Type GetCSharpType()
        {
            switch (Length)
            {
                case { } n when n <= 8:
                    return typeof(byte);
                case { } n when n <= 16:
                    return typeof(ushort);
                case { } n when n <= 32:
                    return typeof(uint);
                case { } n when n <= 64:
                    return typeof(ulong);
                default:
                    return typeof(UInt256);
            }
        }
    }
}
