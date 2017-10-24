using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Abi
{
    public class AbiDynamicBytes : AbiType
    {
        private const int PaddingMultiple = 32;

        public static AbiDynamicBytes Instance = new AbiDynamicBytes();

        private AbiDynamicBytes()
        {
        }

        public override bool IsDynamic => true;

        public override string Name => "bytes";

        public override Type CSharpType { get; } = typeof(byte[]);

        public override (object, int) Decode(byte[] data, int position)
        {
            (BigInteger length, int currentPosition) = UInt.DecodeUInt(data, position);
            int paddingSize = (1 + (int) length / PaddingMultiple) * PaddingMultiple;
            return (data.Slice(currentPosition, (int) length), currentPosition + paddingSize);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is byte[] input)
            {
                int paddingSize = (1 + input.Length / PaddingMultiple) * PaddingMultiple;
                byte[] lengthEncoded = UInt.Encode(new BigInteger(input.Length));
                return Core.Sugar.Bytes.Concat(lengthEncoded, Core.Sugar.Bytes.PadRight(input, paddingSize));
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }
    }
}