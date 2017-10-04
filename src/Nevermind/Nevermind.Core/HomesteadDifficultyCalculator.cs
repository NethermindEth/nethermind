using System.Numerics;

namespace Nevermind.Core
{
    public class HomesteadDifficultyCalculator : FrontierDifficultyCalculator
    {
        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber)
        {
            BigInteger timeAdjustment = BigInteger.Max(1 - BigInteger.Divide(currentTimestamp - parentTimestamp, 10), -99);
            return timeAdjustment;
        }
    }
}