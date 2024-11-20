// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiInt : AbiType
    {
        private const int MaxSize = 256;
        private const int MinSize = 0;

        public static new readonly AbiInt Int8 = new(8);
        public static new readonly AbiInt Int16 = new(16);
        public static new readonly AbiInt Int32 = new(32);
        public static new readonly AbiInt Int64 = new(64);
        public static new readonly AbiInt Int96 = new(96);
        public static new readonly AbiInt Int256 = new(256);

        public AbiInt(int length)
        {
            ThrowIfNotMultipleOf8(length);

            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxSize);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, MinSize);

            Length = length;
            Name = $"int{Length}";
            CSharpType = GetCSharpType();
        }

        public int Length { get; }

        public int LengthInBytes => Length / 8;

        public override string Name { get; }

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            var (value, length) = DecodeInt(data, position, packed);

            return Length switch
            {
                { } n when n <= 8 => ((object, int))((sbyte)value, length),
                { } n when n <= 16 => ((object, int))((short)value, length),
                { } n when n <= 32 => ((object, int))((int)value, length),
                { } n when n <= 64 => ((object, int))((long)value, length),
                _ => ((object, int))(value, length),
            };
        }

        public (BigInteger, int) DecodeInt(byte[] data, int position, bool packed)
        {
            int length = (packed ? LengthInBytes : Int256.LengthInBytes);
            byte[] input = data.Slice(position, length);
            return (input.ToSignedBigInteger(length), position + length);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is BigInteger input)
            {
                return input.ToBigEndianByteArray(32);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(BigInteger);

        private Type GetCSharpType()
        {
            return Length switch
            {
                { } n when n <= 8 => typeof(sbyte),
                { } n when n <= 16 => typeof(short),
                { } n when n <= 32 => typeof(int),
                { } n when n <= 64 => typeof(long),
                _ => typeof(BigInteger),
            };
        }
    }
}
