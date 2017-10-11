namespace Nevermind.Evm.Abi
{
    public class AbiFunction : AbiBytes
    {
        public AbiFunction() : base(24)
        {
        }

        public override string Name => "function";
    }
}