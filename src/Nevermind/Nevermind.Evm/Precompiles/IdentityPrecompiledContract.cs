using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public class IdentityPrecompiledContract : IPrecompiledContract
    {
        private IdentityPrecompiledContract()
        {
        }

        public static IPrecompiledContract Instance = new IdentityPrecompiledContract();

        public BigInteger Address => 4;

        public ulong GasCost(byte[] inputData)
        {
            return 15 + 3 * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return inputData;
        }
    }
}