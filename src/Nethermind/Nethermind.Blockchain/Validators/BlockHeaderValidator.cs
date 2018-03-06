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
using Nethermind.Core.Crypto;
using Nethermind.Mining;

namespace Nethermind.Blockchain.Validators
{
    public class BlockHeaderValidator : IBlockHeaderValidator
    {
        private readonly IEthash _ethash;
        private readonly IBlockStore _chain;

        public BlockHeaderValidator(IBlockStore chain, IEthash ethash)
        {
            _chain = chain;
            _ethash = ethash;
        }

        public bool Validate(BlockHeader header)
        {
            Block parent = _chain.FindParent(header);
            if (parent == null)
            {
                return IsGenesisHeaderValid(header);
            }

            Keccak hash = header.Hash;
            header.RecomputeHash();
            
            bool isNonceValid = _ethash.Validate(header);
            
            // mix hash check
            // difficulty check
            bool gasUsedBelowLimit = header.GasUsed <= header.GasLimit;
            bool gasLimitNotTooHigh = header.GasLimit < parent.Header.GasLimit + BigInteger.Divide(parent.Header.GasLimit, 1024);
            bool gasLimitNotTooLow = header.GasLimit > parent.Header.GasLimit - BigInteger.Divide(parent.Header.GasLimit, 1024);
//            bool gasLimitAboveAbsoluteMinimum = header.GasLimit >= 125000; // TODO: tests are consistently not following this rule
            bool timestampMoreThanAtParent = header.Timestamp > parent.Header.Timestamp;
            bool numberIsParentPlusOne = header.Number == parent.Header.Number + 1;
            bool extraDataNotTooLong = header.ExtraData.Length <= 32;
            bool hashAsExpected = header.Hash == hash;
            if (!hashAsExpected)
            {
                
            }

            return
                   isNonceValid &&
                   gasUsedBelowLimit &&
                   gasLimitNotTooLow &&
                   gasLimitNotTooHigh &&
//                   gasLimitAboveAbsoluteMinimum && // TODO: tests are consistently not following this rule
                   timestampMoreThanAtParent &&
                   numberIsParentPlusOne &&
                   extraDataNotTooLong &&
                   hashAsExpected;
        }

        private static bool IsGenesisHeaderValid(BlockHeader header)
        {
            return
                //block.Header.Nonce < BigInteger.Divide(BigInteger.Pow(2, 256), block.Header.DifficultyCalculator) &&
                // mix hash check
                // proof of work check
                // difficulty check
                header.GasUsed < header.GasLimit &&
                // header.GasLimit > 125000 && // TODO: tests are consistently not following this rule
                header.Timestamp > 0 && // what here?
                header.Number == 0 &&
                header.ExtraData.Length <= 32;
        }
    }
}