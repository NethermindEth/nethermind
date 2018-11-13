/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using Nethermind.Core.Extensions;

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
        }

        public int Length { get; }

        public int LengthInBytes => Length / 8;

        public override string Name => $"uint{Length}";

        public override (object, int) Decode(byte[] data, int position)
        {
            BigInteger lengthData = data.Slice(position, LengthInBytes).ToUnsignedBigInteger();
            return (lengthData, position + LengthInBytes);
        }

        public (BigInteger, int) DecodeUInt(byte[] data, int position)
        {
            return ((BigInteger, int))Decode(data, position);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is BigInteger input)
            {
                return input.ToBigEndianByteArray().PadLeft(UInt.LengthInBytes);
            }

            if (arg is int intInput)
            {
                return intInput.ToBigEndianByteArray().PadLeft(UInt.LengthInBytes);
            }

            if (arg is long longInput)
            {
                return longInput.ToBigEndianByteArray().PadLeft(UInt.LengthInBytes);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(BigInteger);
    }
}