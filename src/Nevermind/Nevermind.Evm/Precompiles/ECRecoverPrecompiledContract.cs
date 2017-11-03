using System;
using System.Numerics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Signing;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Precompiles
{
    public class ECRecoverPrecompiledContract : IPrecompiledContract
    {
        private ECRecoverPrecompiledContract()
        {
        }

        public static IPrecompiledContract Instance = new ECRecoverPrecompiledContract();

        public BigInteger Address => 1;
        public ulong GasCost(byte[] inputData)
        {
            return 3000;
        }

        public byte[] Run(byte[] inputData)
        {
            // TODO:
            try
            {
                Keccak hash = new Keccak(inputData.Slice(0, 32));
                byte[] v = inputData.Slice(32, 32);
                byte[] r = inputData.Slice(64, 32);
                byte[] s = inputData.Slice(96, 32);
                Signature signature = new Signature(r, s, v[31]);
                return ((byte[])Signer.RecoverSignerAddress(signature, hash).Hex).PadLeft(32); // change recovery code to return bytes?
            }
            catch (Exception)
            {
                return new byte[0];
            }
        }
    }
}