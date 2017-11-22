using System.Numerics;
using System.Security.Cryptography;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Precompiles
{
    public class Ripemd160PrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new Ripemd160PrecompiledContract();

        private static RIPEMD160 _ripemd;

        private Ripemd160PrecompiledContract()
        {
            _ripemd = RIPEMD160.Create();
            _ripemd.Initialize();
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
            return _ripemd.ComputeHash(inputData).PadLeft(32);
        }
    }
}