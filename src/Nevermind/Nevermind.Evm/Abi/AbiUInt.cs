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
                return Bytes.PadLeft(input.ToBigEndianByteArray(), UInt.LengthInBytes);
            }

            if (arg is int intInput)
            {
                return Bytes.PadLeft(intInput.ToBigEndianByteArray(), UInt.LengthInBytes);
            }

            if (arg is long longInput)
            {
                return Bytes.PadLeft(longInput.ToBigEndianByteArray(), UInt.LengthInBytes);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(BigInteger);
    }
}