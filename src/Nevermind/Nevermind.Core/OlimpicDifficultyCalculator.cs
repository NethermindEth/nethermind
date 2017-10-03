using System.Numerics;

namespace Nevermind.Core
{
    public class OlimpicDifficultyCalculator : FrontierDifficultyCalculator
    {
        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, ulong blockNumber)
        {
            BigInteger timeAdjustment = currentTimestamp < parentTimestamp + 7 ? 1 : -1;
            return timeAdjustment;
        }
    }
}