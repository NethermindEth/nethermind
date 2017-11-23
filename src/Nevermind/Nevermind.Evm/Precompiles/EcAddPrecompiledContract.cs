using System;
using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public class EcAddPrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new EcAddPrecompiledContract();

        private EcAddPrecompiledContract()
        {
        }

        public BigInteger Address => 6;

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