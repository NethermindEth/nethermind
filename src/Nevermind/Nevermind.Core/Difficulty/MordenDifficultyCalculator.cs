using System.Numerics;

namespace Nevermind.Core.Difficulty
{
    public class MordenDifficultyCalculator : FrontierDifficultyCalculator
    {
        public const long HomesteadBlockNumber = 500000; // more or less?

        private readonly HomesteadDifficultyCalculator _homestead = new HomesteadDifficultyCalculator();

        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber, bool parentHasUncles)
        {
            if (blockNumber < HomesteadBlockNumber)
            {
                return base.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
            }

            return _homestead.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
        }
    }
}