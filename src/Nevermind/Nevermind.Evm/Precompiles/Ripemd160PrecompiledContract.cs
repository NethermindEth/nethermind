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

        public BigInteger Address => 2;

        public ulong GasCost(byte[] inputData)
        {
            // TODO: check why test assumes 0 cost - it the empty inputData assumption correct?
            if(inputData.Length == 0)
            {
                return 0;
            }

            return 600 + 120 * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return Bytes.PadLeft(Ripemd.Compute(inputData), 32);
        }
    }
}