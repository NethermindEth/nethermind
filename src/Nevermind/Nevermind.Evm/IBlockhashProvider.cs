using System.Numerics;
using Nevermind.Core.Crypto;

namespace Nevermind.Evm
{
    public interface IBlockhashProvider
    {
        Keccak? GetBlockhash(Keccak blockHash, BigInteger number);
    }
}