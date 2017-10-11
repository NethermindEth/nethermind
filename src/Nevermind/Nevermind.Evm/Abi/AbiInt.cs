using System;
using Nevermind.Core.Sugar;

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

        public override string Name => $"int{Length}";

        public override (byte[], int) Decode(byte[] data, int position)
        {
            return (data.Slice(position, Length), position + Length);
        }
    }
}