using System.Numerics;

namespace Nevermind.Core.Difficulty
{
    public class OlimpicDifficultyCalculator : FrontierDifficultyCalculator
    {
        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber)
        {
            BigInteger timeAdjustment = currentTimestamp < parentTimestamp + 7 ? 1 : -1;
            return timeAdjustment;
        }
    }
}