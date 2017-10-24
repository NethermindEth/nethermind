using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Abi
{
    public class AbiDynamicBytes : AbiType
    {
        public static AbiDynamicBytes Instance = new AbiDynamicBytes();

        private AbiDynamicBytes()
        {
        }

        public override bool IsDynamic => true;

        public override string Name => "bytes";

        public override (object, int) Decode(byte[] data, int position)
        {
            (BigInteger length, int currentPosition) = AbiUInt.DecodeUInt(data, position);
            int paddingSize = (1 + (int)length / 32) * 32;
            return (data.Slice(currentPosition, (int) length), currentPosition + paddingSize);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is byte[] input)
            {
                int paddingSize = (1 + input.Length / 32) * 32;
                byte[] lengthEncoded = AbiUInt.EncodeUInt(input.Length);
                return Core.Sugar.Bytes.Concat(lengthEncoded, Core.Sugar.Bytes.PadRight(input, paddingSize));
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }
    }
}