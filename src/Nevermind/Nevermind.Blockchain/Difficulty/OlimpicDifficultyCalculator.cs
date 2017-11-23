using System.Numerics;

namespace Nevermind.Blockchain.Difficulty
{
    public class OlimpicDifficultyCalculator : FrontierDifficultyCalculator
    {
        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber, bool parentHasUncles)
        {
            BigInteger timeAdjustment = currentTimestamp < parentTimestamp + 7 ? 1 : -1;
            return timeAdjustment;
        }
    }
}