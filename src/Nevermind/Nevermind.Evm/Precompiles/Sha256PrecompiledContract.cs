using System.Numerics;
using Nevermind.Core.Encoding;

namespace Nevermind.Evm.Precompiles
{
    public class Sha256PrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new Sha256PrecompiledContract();

        private Sha256PrecompiledContract()
        {
        }

        public BigInteger Address => 2;

        public ulong BaseGasCost()
        {
            return 60UL;
        }

        public ulong DataGasCost(byte[] inputData)
        {
            return 12UL * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return Sha2.Compute(inputData);
        }
    }
}