using System;
using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public class ModExpPrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new ModExpPrecompiledContract();

        private ModExpPrecompiledContract()
        {
        }

        public BigInteger Address => 5;

        public ulong BaseGasCost()
        {
            throw new NotImplementedException();
        }

        public ulong DataGasCost(byte[] inputData)
        {
            throw new NotImplementedException();
        }

        public byte[] Run(byte[] inputData)
        {
            throw new NotImplementedException();
        }
    }
}