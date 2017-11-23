using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class Block
    {
        // TODO: genesis
        public Block()
        {
            
        }
        
        public Block(BlockHeader blockHeader, Block parent, params BlockHeader[] ommers)
        {
            Header = blockHeader;
            Parent = parent;
            Ommers = ommers;
            Transactions = new List<Transaction>();
            Receipts = new List<TransactionReceipt>();
        }

        public BlockHeader Header { get; }
        public List<Transaction> Transactions { get; set; }
        public List<TransactionReceipt> Receipts { get; set; }
        public BlockHeader[] Ommers { get; }
        public Block Parent { get; }
        public BigInteger TotalDifficulty => Header.Difficulty + (Parent?.TotalDifficulty ?? 0);
        public Keccak Hash => Keccak.Compute(Rlp.Encode((object)Header));
    }
}