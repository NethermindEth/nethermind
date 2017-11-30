using System.Collections.Generic;
using Nevermind.Core.Crypto;

namespace Nevermind.Core
{
    public class Block
    {
        public Block(BlockHeader blockHeader, params BlockHeader[] ommers)
        {
            Header = blockHeader;
            Ommers = ommers;
            Transactions = new List<Transaction>();
            Receipts = new List<TransactionReceipt>();
        }

        public bool IsGenesis => Header.Number == 0;
        public BlockHeader Header { get; }
        public List<Transaction> Transactions { get; set; }
        public List<TransactionReceipt> Receipts { get; set; }
        public BlockHeader[] Ommers { get; }
        public Keccak Hash => Header.Hash;
    }
}