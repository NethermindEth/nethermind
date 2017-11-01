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

        public BigInteger Address => 3;

        public BigInteger GasCost(byte[] inputData)
        {
            return 60 + 12 * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return Sha2.Compute(inputData);
        }
    }
}