using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain
{
    public interface IBlockProcessor
    {
        Block ProcessBlock(Block parent, BigInteger timestamp, Address beneficiary, long gasLimit, byte[] extraData, List<Transaction> transactions, Keccak mixHash, ulong nonce, params BlockHeader[] uncles);
    }
}