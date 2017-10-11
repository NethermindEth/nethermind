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

        public override (byte[], int) Decode(byte[] data, int position)
        {
            // this is incorrect
            int currentPosition = position;
            BigInteger length;
            (length, currentPosition) = AbiUInt.DecodeLength(data, currentPosition);
            BigInteger totalLength = length * 32;
            for (int i = 0; i < length; i++)
            {
                BigInteger currentLength;
                (currentLength, currentPosition) = AbiUInt.DecodeLength(data, currentPosition);
                totalLength += currentLength;
            }

            return (data.Slice(position, (int) totalLength), currentPosition);
        }
    }
}