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