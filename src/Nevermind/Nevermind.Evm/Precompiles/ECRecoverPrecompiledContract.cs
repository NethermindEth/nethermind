using System;
using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public class ECRecoverPrecompiledContract : IPrecompiledContract
    {
        private ECRecoverPrecompiledContract()
        {
        }

        public static IPrecompiledContract Instance = new ECRecoverPrecompiledContract();

        public BigInteger Address => 2;
        public BigInteger GasCost(byte[] inputData)
        {
            return 3000;
        }

        public byte[] Run(byte[] inputData)
        {
            throw new NotImplementedException();
        }
    }
}