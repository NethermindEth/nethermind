using System.Numerics;

namespace Nevermind.Core.Difficulty
{
    public class HomesteadDifficultyCalculator : FrontierDifficultyCalculator
    {
        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber, bool parentHasUncles)
        {
            BigInteger timeAdjustment = BigInteger.Max(1 - BigInteger.Divide(currentTimestamp - parentTimestamp, 10), -99);
            return timeAdjustment;
        }
    }
}