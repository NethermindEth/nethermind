using System;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Abi
{
    public class AbiBytes : AbiType
    {
        private const int MaxLength = 32;
        private const int MinLength = 0;

        public AbiBytes(int length)
        {
            if (length > MaxLength)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiBytes)} has to be less or equal to {MaxLength}");
            }

            if (length <= MinLength)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiBytes)} has to be greater than {MinLength}");
            }

            Length = length;
        }

        public int Length { get; }

        public override string Name => $"bytes{Length}";

        public override (object, int) Decode(byte[] data, int position)
        {
            return (data.Slice(position, Length), position + Length);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is byte[] input)
            {
                if (input.Length != Length)
                {
                    throw new AbiException(AbiEncodingExceptionMessage);
                }

                return input;
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }
    }
}