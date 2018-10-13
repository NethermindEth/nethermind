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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Mining.Difficulty
{
    public class DifficultyCalculator : IDifficultyCalculator
    {
        private readonly ISpecProvider _specProvider;

        public DifficultyCalculator(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }

        private const long OfGenesisBlock = 131_072;

        public UInt256 Calculate(
            UInt256 parentDifficulty,
            UInt256 parentTimestamp,
            UInt256 currentTimestamp,
            UInt256 blockNumber,
            bool parentHasUncles)
        {
            IReleaseSpec spec = _specProvider.GetSpec(blockNumber);
            BigInteger baseIncrease = BigInteger.Divide(parentDifficulty, 2048);
            BigInteger timeAdjustment = TimeAdjustment(spec, parentTimestamp, currentTimestamp, parentHasUncles);
            BigInteger timeBomb = TimeBomb(spec, blockNumber);
            return (UInt256)BigInteger.Max(
                OfGenesisBlock,
                parentDifficulty +
                timeAdjustment * baseIncrease +
                timeBomb);
        }

        public virtual BigInteger Calculate(BlockHeader blockHeader, BlockHeader parentBlockHeader, bool parentHasUncles)
        {
            if (parentBlockHeader == null)
            {
                return blockHeader.Difficulty;
            }

            return Calculate(
                parentBlockHeader.Difficulty,
                parentBlockHeader.Timestamp,
                blockHeader.Timestamp,
                blockHeader.Number,
                parentHasUncles);
        }

        protected internal virtual BigInteger TimeAdjustment(
            IReleaseSpec spec,
            BigInteger parentTimestamp,
            BigInteger currentTimestamp,
            bool parentHasUncles)
        {
            if (spec.IsEip100Enabled)
            {
                return BigInteger.Max((parentHasUncles ? 2 : BigInteger.One) - BigInteger.Divide(currentTimestamp - parentTimestamp, 9), -99);
            }
            
            if(spec.IsEip2Enabled)
            {
                return BigInteger.Max(BigInteger.One - BigInteger.Divide(currentTimestamp - parentTimestamp, 10), -99);    
            }
            
            if (spec.IsTimeAdjustmentPostOlympic)
            {
                return currentTimestamp < parentTimestamp + 13 ? BigInteger.One : BigInteger.MinusOne;                
            }
            
            return currentTimestamp < parentTimestamp + 7 ? BigInteger.One : BigInteger.MinusOne;
        }

        private BigInteger TimeBomb(IReleaseSpec spec, UInt256 blockNumber)
        {   
            if (spec.IsEip1234Enabled)
            {
                blockNumber = blockNumber - UInt256.Min(blockNumber, 5000000);
            }
            else if (spec.IsEip649Enabled)
            {
                blockNumber = blockNumber - UInt256.Min(blockNumber, 3000000);
            }

            return blockNumber < 200000 ? UInt256.Zero : BigInteger.Pow(2, (int)(BigInteger.Divide(blockNumber, 100000) - 2));
        }
    }
}