using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Evm;

namespace Ethereum.Test.Base
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public Keccak? GetBlockhash(Keccak blockHash, BigInteger number)
        {
            return Keccak.Compute(number.ToString());
        }
    }
}