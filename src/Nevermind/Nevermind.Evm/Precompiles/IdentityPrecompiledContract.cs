using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public class IdentityPrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new IdentityPrecompiledContract();

        private IdentityPrecompiledContract()
        {
        }

        public BigInteger Address => 4;

        public ulong BaseGasCost()
        {
            return 15UL;
        }

        public ulong DataGasCost(byte[] inputData)
        {
            return 3UL * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return inputData;
        }
    }

    public class ModExpPrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new ModExpPrecompiledContract();

        private ModExpPrecompiledContract()
        {
        }

        public BigInteger Address => 5;

        public ulong BaseGasCost()
        {
            return 15UL;
        }

        public ulong DataGasCost(byte[] inputData)
        {
            return 3UL * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return inputData;
        }
    }
}