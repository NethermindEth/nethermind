using System;
using System.Numerics;
using System.Xml.Schema;

namespace Nevermind.Core
{
    public class Block
    {
        private const long GenesisBlockDifficulty = 131072;

        public Block(Block parentBlock, Transaction[] transactions, BlockHeader[] ommers)
        {
            Header = new BlockHeader();
            Transactions = transactions;
            Ommers = ommers;
            
            // set timestamp

            if (parentBlock == null)
            {
                Header.Difficulty = GenesisBlockDifficulty;
                Header.ParentHash = null;
                //Header.GasLimit = 
            }
            else
            {
                Header.Difficulty = BigInteger.Max(
                    GenesisBlockDifficulty,
                    parentBlock.Header.Difficulty +
                    TimeAdjustment(this, parentBlock) +
                    TimeBomb(this) +
                    BigInteger.Divide(parentBlock.Header.Difficulty, 2048));
                Header.ParentHash = parentBlock.Header.MixHash;
            }
        }

        private long TimeAdjustment(Block parentBlock, Block block)
        {
            return Math.Max(1 - (block.Header.Timestamp - parentBlock.Header.Timestamp) / 10, 99);
        }

        private BigInteger TimeBomb(Block  block)
        {
            BigInteger timeBomb = 2;
            BigInteger.Pow(timeBomb, (int)(Math.Floor(block.Header.Number / 100000m) - 2m));
            return timeBomb;
        }

        public BlockHeader Header { get; set; }
        public Transaction[] Transactions { get; set; }
        public BlockHeader[] Ommers { get; set; }
    }
}