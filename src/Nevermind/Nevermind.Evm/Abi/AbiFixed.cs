using System;

namespace Nevermind.Evm.Abi
{
    public class AbiFixed : AbiType
    {
        public static AbiFixed Standard { get; }= new AbiFixed(128, 19);

        private const int MaxLength = 256;
        private const int MinLength = 0;

        private const int MaxPrecision = 80;
        private const int MinPrecision = 0;

        public AbiFixed(int length, int precision)
        {
            if (length % 8 != 0)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiFixed)} has to be a multiple of 8");
            }

            if (length > MaxLength)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiFixed)} has to be less or equal to {MaxLength}");
            }

            if (length <= MinLength)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiFixed)} has to be greater than {MinLength}");
            }

            if (precision > MaxPrecision)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(precision)} of {nameof(AbiFixed)} has to be less or equal to {MaxPrecision}");
            }

            if (precision <= MinPrecision)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(precision)} of {nameof(AbiFixed)} has to be greater than {MinPrecision}");
            }

            Length = length;
        }

        public override string Name => $"fixed{Length}x{Precision}";

        public int Length { get; set; }
        public int Precision { get; set; }

        public override (object, int) Decode(byte[] data, int position)
        {
            throw new NotImplementedException();
        }

        public override byte[] Encode(object arg)
        {
            throw new NotImplementedException();
        }
    }
}