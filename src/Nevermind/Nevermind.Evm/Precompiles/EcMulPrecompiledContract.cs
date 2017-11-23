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

        public long BaseGasCost()
        {
            throw new NotImplementedException();
        }

        public long DataGasCost(byte[] inputData)
        {
            throw new NotImplementedException();
        }

        public byte[] Run(byte[] inputData)
        {
            throw new NotImplementedException();
        }
    }
}