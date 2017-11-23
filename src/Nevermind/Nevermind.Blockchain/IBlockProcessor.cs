using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;

namespace Nevermind.Blockchain
{
    public interface IBlockProcessor
    {
        Block ProcessBlock(Block parent, BigInteger timestamp, Address beneficiary, long gasLimit, byte[] extraData, List<Transaction> transactions, params BlockHeader[] uncles);
    }
}