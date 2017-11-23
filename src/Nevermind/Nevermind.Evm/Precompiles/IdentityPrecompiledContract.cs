using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public class IdentityPrecompiledContract : IPrecompiledContract
    {
        public static readonly IPrecompiledContract Instance = new IdentityPrecompiledContract();

        private IdentityPrecompiledContract()
        {
        }

        public BigInteger Address => 4;

        public long BaseGasCost()
        {
            return 15L;
        }

        public long DataGasCost(byte[] inputData)
        {
            return 3L * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return inputData;
        }
    }
}