using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Abi
{
    public class AbiArray : AbiType
    {
        private readonly AbiType _elementType;

        public AbiArray(AbiType elementType)
        {
            _elementType = elementType;
        }

        public override bool IsDynamic => true;

        public override string Name => $"{_elementType}[]";

        public override (object, int) Decode(byte[] data, int position)
        {
            // this is incorrect
            int currentPosition = position;
            BigInteger length;
            (length, currentPosition) = AbiUInt.DecodeUInt(data, currentPosition);
            BigInteger totalLength = length * 32;
            for (int i = 0; i < length; i++)
            {
                BigInteger currentLength;
                (currentLength, currentPosition) = AbiUInt.DecodeUInt(data, currentPosition);
                totalLength += currentLength;
            }

            return (data.Slice(position, (int) totalLength), currentPosition);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is Array array)
            {
                int i = 0;
                byte[][] encodedItems = new byte[array.Length][];
                foreach (object o in array)
                {
                    encodedItems[i++] = _elementType.Encode(o);
                }

                return Core.Sugar.Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }
    }
}