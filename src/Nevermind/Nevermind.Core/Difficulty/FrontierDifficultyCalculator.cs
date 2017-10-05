using System.Numerics;

namespace Nevermind.Core.Difficulty
{
    public class FrontierDifficultyCalculator : IDifficultyCalculator
    {
        public const long OfGenesisBlock = 131_072;

        //public const long OfGenesisBlock = 17_179_869_184;

        public BigInteger Calculate(
            BigInteger parentDifficulty,
            BigInteger parentTimestamp,
            BigInteger currentTimestamp,
            BigInteger blockNumber)
        {
            BigInteger baseIncrease = BigInteger.Divide(parentDifficulty, 2048);
            BigInteger timeAdjustment = TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber);
            BigInteger timeBomb = TimeBomb(blockNumber);
            return BigInteger.Max(
                OfGenesisBlock,
                parentDifficulty +
                timeAdjustment * baseIncrease +
                timeBomb);
        }

        public virtual BigInteger Calculate(BlockHeader blockHeader, BlockHeader parentBlockHeader)
        {
            if (parentBlockHeader == null)
            {
                return OfGenesisBlock;
            }

            return Calculate(
                parentBlockHeader.Difficulty,
                parentBlockHeader.Timestamp,
                blockHeader.Timestamp,
                blockHeader.Number);
        }

        protected internal virtual BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp,
            BigInteger blockNumber)
        {
#if DEBUG
            BigInteger difference = parentTimestamp - currentTimestamp;
#endif
            BigInteger timeAdjustment = currentTimestamp < parentTimestamp + 13 ? 1 : -1;
            return timeAdjustment;
        }

        protected internal virtual BigInteger TimeBomb(BigInteger blockNumber)
        {
            if (blockNumber < 200000)
            {
                return 0;
            }

            BigInteger timeBomb = BigInteger.Pow(2, (int)(BigInteger.Divide(blockNumber, 100000) - 2));
            return timeBomb;
        }
    }
}