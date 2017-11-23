using System.Numerics;
using System.Security.Cryptography;
using Nevermind.Core.Extensions;

namespace Nevermind.Evm.Precompiles
{
    public class Ripemd160PrecompiledContract : IPrecompiledContract
    {
        public static readonly IPrecompiledContract Instance = new Ripemd160PrecompiledContract();

        private static RIPEMD160 _ripemd;

        private Ripemd160PrecompiledContract()
        {
            _ripemd = RIPEMD160.Create();
            _ripemd.Initialize();
        }

        public BigInteger Address => 3;

        public long BaseGasCost()
        {
            return 600L;
        }

        public long DataGasCost(byte[] inputData)
        {
            return 120L * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return _ripemd.ComputeHash(inputData).PadLeft(32);
        }
    }
}