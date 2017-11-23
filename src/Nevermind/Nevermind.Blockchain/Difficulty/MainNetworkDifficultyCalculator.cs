using System.Numerics;

namespace Nevermind.Blockchain.Difficulty
{
    public class MainNetworkDifficultyCalculator : FrontierDifficultyCalculator
    {
        public const long HomesteadBlockNumber = 1150000;

        public const long ByzantiumBlockNumber = 4370000;

        private readonly HomesteadDifficultyCalculator _homestead = new HomesteadDifficultyCalculator();

        private readonly ByzantiumDifficultyCalculator _byzantium = new ByzantiumDifficultyCalculator();

        protected internal override BigInteger TimeAdjustment(BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber, bool parentHasUncles)
        {
            if (blockNumber < HomesteadBlockNumber)
            {
                return base.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
            }

            if (blockNumber < ByzantiumBlockNumber)
            {
                return _homestead.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
            }

            return _byzantium.TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
        }

        protected internal override BigInteger TimeBomb(BigInteger blockNumber)
        {
            if (blockNumber < ByzantiumBlockNumber)
            {
                return base.TimeBomb(blockNumber);
            }

            return _byzantium.TimeBomb(blockNumber);
        }
    }
}