﻿/*
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
using MathNet.Numerics;

namespace Nethermind.Abi
{
    public class AbiUFixed : AbiType
    {
        private const int MaxLength = 256;
        private const int MinLength = 0;

        private const int MaxPrecision = 80;
        private const int MinPrecision = 0;

        private readonly BigInteger _denominator;

        public AbiUFixed(int length, int precision)
        {
            if (length % 8 != 0)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiUFixed)} has to be a multiple of 8");
            }

            if (length > MaxLength)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiUFixed)} has to be less or equal to {MaxLength}");
            }

            if (length <= MinLength)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiUFixed)} has to be greater than {MinLength}");
            }

            if (precision > MaxPrecision)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(precision)} of {nameof(AbiUFixed)} has to be less or equal to {MaxPrecision}");
            }

            if (precision <= MinPrecision)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(precision)} of {nameof(AbiUFixed)} has to be greater than {MinPrecision}");
            }

            Length = length;
            Precision = precision;

            _denominator = BigInteger.Pow(10, Precision);
        }

        public override string Name => $"ufixed{Length}x{Precision}";

        public int Length { get; }
        public int Precision { get; }

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            (BigInteger nominator, int newPosition) = Int256.DecodeInt(data, position, packed);
            BigRational rational = BigRational.FromBigInt(nominator) * BigRational.Reciprocal(BigRational.Pow(BigRational.FromInt(10), Precision));
            return (rational, newPosition);
        }

        public override byte[] Encode(object arg, bool packed)
        {
            if (arg is BigRational input)
            {
                if (_denominator != input.Denominator)
                {
                    throw new AbiException(AbiEncodingExceptionMessage);
                }

                return Int256.Encode(input.Numerator, packed);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(BigRational);
    }
}