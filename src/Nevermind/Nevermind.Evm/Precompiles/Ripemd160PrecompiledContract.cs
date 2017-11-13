using System.Numerics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Precompiles
{
    public class Ripemd160PrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new Ripemd160PrecompiledContract();

        private Ripemd160PrecompiledContract()
        {
        }

        public BigInteger Address => 3;
        public ulong BaseGasCost()
        {
            return 600UL;
        }

        public ulong DataGasCost(byte[] inputData)
        {
            return 120UL * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return Bytes.PadLeft(Ripemd.Compute(inputData), 32);
        }
    }
}