// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using MathNet.Numerics;

namespace Nethermind.Abi
{
    public class AbiFixed : AbiType
    {
        public static readonly AbiFixed Standard = new(128, 19);

        private const int MaxLength = 256;
        private const int MinLength = 0;

        private const int MaxPrecision = 80;
        private const int MinPrecision = 0;

        public AbiFixed(int length, int precision)
        {
            ThrowIfNotMultipleOf8(length);

            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxLength);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, MinLength);

            ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, MaxPrecision);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(precision, MinPrecision);

            Length = length;
            Precision = precision;
            Name = $"fixed{Length}x{Precision}";
            _denominator = BigInteger.Pow(10, Precision);
        }

        public override string Name { get; }

        public int Length { get; }
        public int Precision { get; }

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            (BigInteger nominator, int newPosition) = Int256.DecodeInt(data, position, packed);
            BigRational rational = BigRational.FromBigInt(nominator) * BigRational.Reciprocal(BigRational.Pow(BigRational.FromInt(10), Precision));
            return (rational, newPosition);
        }

        private readonly BigInteger _denominator;

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is BigRational input)
            {
                if (_denominator != input.Denominator)
                {
                    throw new AbiException(AbiEncodingExceptionMessage);
                }

                return UInt256.Encode(input.Numerator, packed);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(BigRational);
    }
}
