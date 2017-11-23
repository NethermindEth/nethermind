using System.Collections.Generic;

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

        public BlockHeader Header { get; }
        public List<Transaction> Transactions { get; set; }
        public List<TransactionReceipt> Receipts { get; set; }
        public BlockHeader[] Ommers { get; }
    }
}