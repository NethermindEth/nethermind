using System;
using System.Numerics;
using Nevermind.Core.Extensions;

namespace Nevermind.Evm.Abi
{
    public class AbiInt : AbiType
    {
        private const int MaxSize = 256;

        private const int MinSize = 0;

        public AbiInt(int length)
        {
            if (length % 8 != 0)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiInt)} has to be a multiple of 8");
            }

            if (length > MaxSize)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiInt)} has to be less or equal to {MinSize}");
            }

            if (length <= MinSize)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiInt)} has to be greater than {MinSize}");
            }

            Length = length;
        }

        public int Length { get; }

        public int LengthInBytes => Length / 8;

        public override string Name => $"int{Length}";

        public override (object, int) Decode(byte[] data, int position)
        {
            byte[] input = data.Slice(position, LengthInBytes);
            return (input.ToSignedBigInteger(), position + LengthInBytes);
        }

        public (BigInteger, int) DecodeInt(byte[] data, int position)
        {
            return ((BigInteger, int))Decode(data, position);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is BigInteger input)
            {
                return input.ToBigEndianByteArray(false, 32);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(BigInteger);
    }
}