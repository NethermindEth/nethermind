//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Ethash
{
    public class EthashDifficultyCalculator : IDifficultyCalculator
    {
        private readonly ISpecProvider _specProvider;

        public EthashDifficultyCalculator(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }

        private const long OfGenesisBlock = 131_072;
        
        public UInt256 Calculate(BlockHeader header, BlockHeader parent) =>
            Calculate(parent.Difficulty, 
                parent.Timestamp, 
                header.Timestamp,
                header.Number,
                parent.OmmersHash != Keccak.OfAnEmptySequenceRlp);

        public UInt256 Calculate(
            UInt256 parentDifficulty,
            UInt256 parentTimestamp,
            UInt256 currentTimestamp,
            long blockNumber,
            bool parentHasUncles)
        {
            IReleaseSpec spec = _specProvider.GetSpec(blockNumber);
            if (spec.FixedDifficulty != null && blockNumber != 0)
            {
                return (UInt256)spec.FixedDifficulty.Value;
            }
            
            BigInteger baseIncrease = BigInteger.Divide((BigInteger)parentDifficulty, spec.DifficultyBoundDivisor);
            BigInteger timeAdjustment = TimeAdjustment(spec, (BigInteger)parentTimestamp, (BigInteger)currentTimestamp, parentHasUncles);
            BigInteger timeBomb = TimeBomb(spec, blockNumber);
            return (UInt256)BigInteger.Max(
                OfGenesisBlock,
                (BigInteger)parentDifficulty +
                timeAdjustment * baseIncrease +
                timeBomb);
        }

        private BigInteger TimeAdjustment(
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

        private BigInteger TimeBomb(IReleaseSpec spec, long blockNumber)
        {
            blockNumber = blockNumber - spec.DifficultyBombDelay;

            // Note: block 200000 is when the difficulty bomb was introduced but we did not spec it in any release info, just hardcoded it
            return blockNumber < 200000 ? BigInteger.Zero : BigInteger.Pow(2, (int)(BigInteger.Divide(blockNumber, 100000) - 2));
        }
    }
}
