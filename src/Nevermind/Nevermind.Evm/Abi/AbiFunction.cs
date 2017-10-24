namespace Nevermind.Evm.Abi
{
    public class AbiFunction : AbiBytes
    {
        private AbiFunction() : base(24)
        {
        }

        public static AbiFunction Instance = new AbiFunction();

        public override string Name => "function";
    }
}