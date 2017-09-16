using System.Numerics;

namespace Nevermind.Core
{
    public class Block
    {
        public Block(Block parentBlock, BlockHeader[] ommers, Transaction[] transactions)
        {
            Header = parentBlock == null ? BlockHeader.Genesis : new BlockHeader(parentBlock.Header, ommers, transactions);
            Parent = parentBlock;
            Ommers = ommers;
            Transactions = transactions;
        }

        public BlockHeader Header { get; }
        public Transaction[] Transactions { get; }
        public BlockHeader[] Ommers { get; }
        public BigInteger TotalDifficulty => Header.Difficulty + (Parent?.TotalDifficulty ?? 0);
        public Block Parent { get; }

        static Block()
        {
            Genesis = new Block(null, new BlockHeader[] { }, new Transaction[] { });
        }

        public static Block Genesis { get; }
    }
}