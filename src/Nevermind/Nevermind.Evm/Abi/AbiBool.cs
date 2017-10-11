namespace Nevermind.Evm.Abi
{
    public class AbiBool : AbiUInt
    {
        internal AbiBool() : base(8)
        {
        }

        public override string Name => "bool";
    }
}