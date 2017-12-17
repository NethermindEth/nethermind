using System;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;
using Nevermind.Core.Potocol;

namespace Nevermind.Evm.Precompiles
{
    public class ECRecoverPrecompiledContract : IPrecompiledContract
    {
        public static readonly IPrecompiledContract Instance = new ECRecoverPrecompiledContract();

        private ECRecoverPrecompiledContract()
        {
        }

        public BigInteger Address => 1;

        public long DataGasCost(byte[] inputData)
        {
            return 0L;
        }

        public long BaseGasCost()
        {
            return 3000L;
        }

        public byte[] Run(byte[] inputData)
        {
            Signer signer = new Signer(Olympic.Instance, ChainId.Mainnet);

            inputData = inputData.PadRight(128);

            Keccak hash = new Keccak(inputData.Slice(0, 32));
            byte[] vBytes = inputData.Slice(32, 32);
            byte[] r = inputData.Slice(64, 32);
            byte[] s = inputData.Slice(96, 32);

            // TEST: CALLCODEEcrecoverV_prefixedf0_d0g0v0
            // TEST: CALLCODEEcrecoverV_prefixedf0_d1g0v0
            for (int i = 0; i < 31; i++)
            {
                if (vBytes[i] != 0)
                {
                    throw new ArgumentException();
                }
            }

            byte v = vBytes[31];
            if (v != 27 && v != 28)
            {
                throw new ArgumentException();
            }

            Signature signature = new Signature(r, s, v);
            return ((byte[])signer.Recover(signature, hash).Hex).PadLeft(32); // TODO: change recovery code to return bytes?
        }
    }
}