using System.Numerics;

namespace Nevermind.Core
{
    public class MordenDifficultyCalculator : FrontierDifficultyCalculator
    {
        public const long HomesteadBlockNumber = 500000; // more or less?

        private readonly HomesteadDifficultyCalculator _homestead = new HomesteadDifficultyCalculator();

        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber)
        {
            if (blockNumber < HomesteadBlockNumber)
            {
                return base.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber);
            }

            return _homestead.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber);
        }
    }
}