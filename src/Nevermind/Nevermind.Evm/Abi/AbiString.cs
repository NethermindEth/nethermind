using System;
using System.Text;

namespace Nevermind.Evm.Abi
{
    public class AbiString : AbiType
    {
        public static AbiString Instance = new AbiString();

        private AbiString()
        {
        }

        public override bool IsDynamic => true;

        public override string Name => "string";

        public override (object, int) Decode(byte[] data, int position)
        {
            (object bytes, int newPosition) = DynamicBytes.Decode(data, position);
            return (Encoding.ASCII.GetString((byte[]) bytes), newPosition);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is string input)
            {
                return DynamicBytes.Encode(Encoding.ASCII.GetBytes(input));
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(string);
    }
}