using Nevermind.Core;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Abi
{
    public class AbiAddress : AbiUInt
    {
        private AbiAddress() : base(160)
        {
        }

        public static AbiAddress Instance = new AbiAddress();

        public override string Name => "address";

        public override byte[] Encode(object arg)
        {
            if (arg is Address input)
            {
                return input.Hex;
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override (object, int) Decode(byte[] data, int position)
        {
            return (new Address(data.Slice(position, 20)), position + 20);
        }
    }
}