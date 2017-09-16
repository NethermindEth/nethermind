using System.Numerics;
using static System.Math;

namespace Nevermind.Core
{
    public static class Difficulty
    {
        public const long OfGenesisBlock = 131072;

        public static BigInteger Calculate(Block block)
        {
            Block parentBlock = block.Parent;
            if (parentBlock == null)
                return OfGenesisBlock;

            BlockHeader parentBlockHeader = parentBlock.Header;
            BlockHeader blockHeader = block.Header;

            return BigInteger.Max(
                OfGenesisBlock,
                parentBlock.Header.Difficulty +
                TimeAdjustment(blockHeader, parentBlockHeader) +
                TimeBomb(blockHeader) +
                BigInteger.Divide(parentBlock.Header.Difficulty, 2048));
        }

        private static BigInteger TimeAdjustment(BlockHeader parentBlockHeader, BlockHeader blockHeader)
        {
            return Max(1 - (blockHeader.Timestamp - parentBlockHeader.Timestamp) / 10, 99);
        }

        private static BigInteger TimeBomb(BlockHeader blockHeader)
        {
            return BigInteger.Pow(2, (int) (Floor(blockHeader.Number / 100000m) - 2m));
        }
    }
}