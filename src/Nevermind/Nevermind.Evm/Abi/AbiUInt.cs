using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Abi
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

        public override (byte[], int) Decode(byte[] data, int position)
        {
            return (data.Slice(position, LengthInBytes), position + LengthInBytes);
        }

        public static (BigInteger, int) DecodeLength(byte[] data, int position)
        {
            BigInteger lengthData = data.Slice(0, 32).ToUnsignedBigInteger();
            return (lengthData, position + 64);
        }
    }
}