using System.Numerics;
using static System.Math;

namespace Nevermind.Core
{
    public static class DifficultyCalculator
    {
        public const long OfGenesisBlock = 131072;

        public static BigInteger Calculate(BlockHeader blockHeader, BlockHeader parentBlockHeader)
        {
            if (parentBlockHeader == null)
                return OfGenesisBlock;

            return BigInteger.Max(
                OfGenesisBlock,
                parentBlockHeader.Difficulty +
                TimeAdjustment(blockHeader, parentBlockHeader) +
                TimeBomb(blockHeader) +
                BigInteger.Divide(blockHeader.Difficulty, 2048));
        }

        private static BigInteger TimeAdjustment(BlockHeader parentBlockHeader, BlockHeader blockHeader)
        {
            return BigInteger.Max(1 - BigInteger.Divide(blockHeader.Timestamp - parentBlockHeader.Timestamp, 10), -99);
        }

        private static BigInteger TimeBomb(BlockHeader blockHeader)
        {
            return BigInteger.Pow(2, (int) (Floor(blockHeader.Number / 100000m) - 2m));
        }
    }
}