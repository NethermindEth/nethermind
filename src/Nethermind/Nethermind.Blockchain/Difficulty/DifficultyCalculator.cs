/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Difficulty
{
    public class DifficultyCalculator : IDifficultyCalculator
    {
        private readonly IReleaseSpec _releaseSpec;

        public DifficultyCalculator(IReleaseSpec releaseSpec, long genesisBlockDiffculty = OfGenesisBlock)
        {
            _releaseSpec = releaseSpec;
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
            
            if (_releaseSpec.IsEip100Enabled)
            {
                return BigInteger.Max((parentHasUncles ? 2 : 1) - BigInteger.Divide(currentTimestamp - parentTimestamp, 9), -99);
            }
            
            if(_releaseSpec.IsEip2Enabled)
            {
                return BigInteger.Max(1 - BigInteger.Divide(currentTimestamp - parentTimestamp, 10), -99);    
            }
            
            if (_releaseSpec.IsTimeAdjustmentPostOlympic)
            {
                return currentTimestamp < parentTimestamp + 13 ? 1 : -1;                
            }
            
            return currentTimestamp < parentTimestamp + 7 ? 1 : -1;
        }

        private BigInteger TimeBomb(BigInteger blockNumber)
        {
            if (_releaseSpec.IsEip649Enabled)
            {
                blockNumber = blockNumber - 3000000;
            }

            return blockNumber < 200000 ? BigInteger.Zero : BigInteger.Pow(2, (int)(BigInteger.Divide(blockNumber, 100000) - 2));
        }
    }
}