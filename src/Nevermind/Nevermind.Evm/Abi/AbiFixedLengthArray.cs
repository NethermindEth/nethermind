using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Abi
{
    public class AbiFixedLengthArray : AbiType
    {
        private readonly AbiType _elementType;

        public AbiFixedLengthArray(AbiType elementType, int length)
        {
            _elementType = elementType;
            Length = length;
        }

        public override bool IsDynamic => Length > 0;

        public int Length { get; }

        public override string Name => $"{_elementType}[{Length}]";

        public override (object, int) Decode(byte[] data, int position)
        {
            // this is incorrect
            BigInteger totalLength = Length * 32;
            int currentPosition = position;
            for (int i = 0; i < Length; i++)
            {
                BigInteger currentLength;
                (currentLength, currentPosition) = AbiUInt.DecodeUInt(data, currentPosition);
                totalLength += currentLength;
            }

            return (data.Slice(position, (int)totalLength), currentPosition);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is Array input)
            {
                if (input.Length != Length)
                {
                    throw new AbiException(AbiEncodingExceptionMessage);
                }

                byte[][] encodedItems = new byte[Length][];
                int i = 0;
                foreach (object o in input)
                {
                    encodedItems[i++] = _elementType.Encode(o);
                }
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }
    }
}