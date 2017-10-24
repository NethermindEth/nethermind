namespace Nevermind.Evm.Abi
{
    public class AbiBool : AbiUInt
    {
        private AbiBool() : base(8)
        {
        }

        public static AbiBool Instance = new AbiBool();

        public override string Name => "bool";

        public override byte[] Encode(object arg)
        {
            if (arg is bool input)
            {
                return new[] {input ? (byte) 1 : (byte) 0};
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override (object, int) Decode(byte[] data, int position)
        {
            return (data[position] == 1, position + 1);
        }
    }
}