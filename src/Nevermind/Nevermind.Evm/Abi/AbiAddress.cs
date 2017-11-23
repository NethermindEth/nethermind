using Nevermind.Core;
using Nevermind.Core.Extensions;

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
                byte[] bytes = input.Hex;
                return UInt.Encode(bytes.ToUnsignedBigInteger());
            }

            if (arg is string stringInput)
            {
                return Encode(new Address(stringInput));
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override (object, int) Decode(byte[] data, int position)
        {
            return (new Address(data.Slice(position + 12, Address.LengthInBytes)), position + UInt.LengthInBytes);
        }
    }
}