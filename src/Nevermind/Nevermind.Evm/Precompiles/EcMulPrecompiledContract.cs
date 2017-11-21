using System;
using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public class EcMulPrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new EcMulPrecompiledContract();

        private EcMulPrecompiledContract()
        {
        }

        public BigInteger Address => 7;

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