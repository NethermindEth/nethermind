using System.Numerics;

namespace Nevermind.Core.Difficulty
{
    public class RopstenDifficultyCalculator : HomesteadDifficultyCalculator
    {
        private readonly ByzantiumDifficultyCalculator _byzantium = new ByzantiumDifficultyCalculator();

        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber,
            bool parentHasUncles)
        {
            if (blockNumber >= 1700000)
            {
                return _byzantium.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
            }

            return base.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
        }

        protected internal override BigInteger TimeBomb(BigInteger blockNumber)
        {
            if (blockNumber >= 1700000)
            {
                return _byzantium.TimeBomb(blockNumber);
            }

            return base.TimeBomb(blockNumber);
        }
    }
}