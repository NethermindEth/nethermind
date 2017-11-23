using System.Numerics;

namespace Nevermind.Blockchain.Difficulty
{
    public class ByzantiumDifficultyCalculator : FrontierDifficultyCalculator
    {
        // EIP-100
        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber, bool parentHasUncles)
        {
            BigInteger timeAdjustment = BigInteger.Max((parentHasUncles ? 2 : 1) - BigInteger.Divide(currentTimestamp - parentTimestamp, 9), -99);
            return timeAdjustment;
        }

        // EIP-649
        protected internal override BigInteger TimeBomb(BigInteger blockNumber)
        {
            return base.TimeBomb(blockNumber - 3000000);
        }
    }
}