using System.Numerics;
using System.Security.Cryptography;

namespace Nevermind.Evm.Precompiles
{
    public class Sha256PrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new Sha256PrecompiledContract();

        private static SHA256 _sha256;

        private Sha256PrecompiledContract()
        {
            _sha256 = SHA256.Create();
            _sha256.Initialize();
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
            return _sha256.ComputeHash(inputData);
        }
    }
}