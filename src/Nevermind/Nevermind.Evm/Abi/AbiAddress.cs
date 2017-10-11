namespace Nevermind.Evm.Abi
{
    public class AbiAddress : AbiUInt
    {
        public AbiAddress() : base(160)
        {
        }

        public override string Name => "address";
    }
}