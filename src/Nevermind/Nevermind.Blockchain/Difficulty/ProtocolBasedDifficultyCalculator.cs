using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Potocol;

namespace Nevermind.Blockchain.Difficulty
{
    public class ProtocolBasedDifficultyCalculator : IDifficultyCalculator
    {
        private readonly IEthereumRelease _ethereumRelease;

        public ProtocolBasedDifficultyCalculator(IEthereumRelease ethereumRelease, long genesisBlockDiffculty = OfGenesisBlock)
        {
            _ethereumRelease = ethereumRelease;
        }

        private const long OfGenesisBlock = 131_072; // Olympic?

        //private const long OfGenesisBlock = 17_179_869_184;

        public BigInteger Calculate(
            BigInteger parentDifficulty,
            BigInteger parentTimestamp,
            BigInteger currentTimestamp,
            BigInteger blockNumber,
            bool parentHasUncles)
        {
            BigInteger baseIncrease = BigInteger.Divide(parentDifficulty, 2048);
            BigInteger timeAdjustment = TimeAdjustment(parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
            BigInteger timeBomb = TimeBomb(blockNumber);
            return BigInteger.Max(
                OfGenesisBlock,
                parentDifficulty +
                timeAdjustment * baseIncrease +
                timeBomb);
        }

        public virtual BigInteger Calculate(BlockHeader blockHeader, BlockHeader parentBlockHeader, bool parentHasUncles)
        {
            if (parentBlockHeader == null)
            {
                return OfGenesisBlock;
            }

            return Calculate(
                parentBlockHeader.Difficulty,
                parentBlockHeader.Timestamp,
                blockHeader.Timestamp,
                blockHeader.Number,
                parentHasUncles);
        }

        protected internal virtual BigInteger TimeAdjustment(
            BigInteger parentTimestamp,
            BigInteger currentTimestamp,
            BigInteger blockNumber,
            bool parentHasUncles)
        {
#if DEBUG
            BigInteger difference = parentTimestamp - currentTimestamp;
#endif
            
            if (_ethereumRelease.IsEip100Enabled)
            {
                return BigInteger.Max((parentHasUncles ? 2 : 1) - BigInteger.Divide(currentTimestamp - parentTimestamp, 9), -99);
            }
            
            if(_ethereumRelease.IsEip2Enabled)
            {
                return BigInteger.Max(1 - BigInteger.Divide(currentTimestamp - parentTimestamp, 10), -99);    
            }
            
            if (_ethereumRelease.IsTimeAdjustmentPostOlympic)
            {
                return currentTimestamp < parentTimestamp + 13 ? 1 : -1;                
            }
            
            return currentTimestamp < parentTimestamp + 7 ? 1 : -1;
        }

        private BigInteger TimeBomb(BigInteger blockNumber)
        {
            if (_ethereumRelease.IsEip649Enabled)
            {
                blockNumber = blockNumber - 3000000;
            }

            return blockNumber < 200000 ? BigInteger.Zero : BigInteger.Pow(2, (int)(BigInteger.Divide(blockNumber, 100000) - 2));
        }
    }
}