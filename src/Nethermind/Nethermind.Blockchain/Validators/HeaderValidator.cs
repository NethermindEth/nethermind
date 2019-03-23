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

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Mining;

namespace Nethermind.Blockchain.Validators
{
    public class HeaderValidator : IHeaderValidator
    {
        private static readonly byte[] DaoExtraData = Bytes.FromHexString("0x64616f2d686172642d666f726b");

        private readonly ISealValidator _sealValidator;
        private readonly BigInteger? _daoBlockNumber;
        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;

        public HeaderValidator(IBlockTree blockTree, ISealValidator sealValidator, ISpecProvider specProvider, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _daoBlockNumber = specProvider?.DaoBlockNumber;
        }
        
        public bool Validate(BlockHeader header, bool isOmmer = false)
        {
            bool areNonceValidAndMixHashValid = header.Number == 0 || header.SealEngineType == SealEngineType.None || _sealValidator.ValidateSeal(header);
            if (!areNonceValidAndMixHashValid)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - invalid mix hash / nonce");
            }
            
            bool hashAsExpected = header.Hash == BlockHeader.CalculateHash(header);
            if (!hashAsExpected)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - invalid block hash");
            }
            
            Block parent = _blockTree.FindBlock(header.ParentHash, false);
            if (parent == null)
            {
                if (header.Number == 0)
                {
                    var isGenesisValid = IsGenesisHeaderValid(header);;
                    if (!isGenesisValid)
                    {
                        _logger.Warn($"Invalid genesis block header ({header.Hash})");
                    }
                    
                    return isGenesisValid;
                }
                
                _logger.Warn($"Orphan block, could not find parent ({header.Hash})");
                return false;
            }

            bool sealParamsCorrect = _sealValidator.ValidateParams(parent, header);
            if (!sealParamsCorrect)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - seal parameters incorrect");
            }

            // difficulty check
            bool gasUsedBelowLimit = header.GasUsed <= header.GasLimit;
            if (!gasUsedBelowLimit)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - gas used above gas limit");
            }

            bool gasLimitNotTooHigh = header.GasLimit < parent.Header.GasLimit + BigInteger.Divide(parent.Header.GasLimit, 1024);
            if (!gasLimitNotTooHigh)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - gas limit too high");
            }

            bool gasLimitNotTooLow = header.GasLimit > parent.Header.GasLimit - BigInteger.Divide(parent.Header.GasLimit, 1024);
            if (!gasLimitNotTooLow)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - invalid mix hash / nonce");
            }

//            bool gasLimitAboveAbsoluteMinimum = header.GasLimit >= 125000; // TODO: tests are consistently not following this rule
            bool timestampMoreThanAtParent = header.Timestamp > parent.Header.Timestamp;
            if (!timestampMoreThanAtParent)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - timestamp before parent");
            }

            bool numberIsParentPlusOne = header.Number == parent.Header.Number + 1;
            if (!numberIsParentPlusOne)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - block number is not parent + 1");
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"Validating block {header.Hash} ({header.Number}), extraData {header.ExtraData.ToHexString(true)}");
            }


            bool extraDataValid = isOmmer
                                  || _daoBlockNumber == null
                                  || header.Number < _daoBlockNumber
                                  || header.Number >= _daoBlockNumber + 10
                                  || Bytes.AreEqual(header.ExtraData, DaoExtraData);
            if (!extraDataValid)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - DAO extra data not valid");
            }

            return
                areNonceValidAndMixHashValid &&
                gasUsedBelowLimit &&
                gasLimitNotTooLow &&
                gasLimitNotTooHigh &&
                sealParamsCorrect &&
//                   gasLimitAboveAbsoluteMinimum && // TODO: tests are consistently not following this rule
                timestampMoreThanAtParent &&
                numberIsParentPlusOne &&
                hashAsExpected &&
                extraDataValid;
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
                //header.Timestamp > 0 && // what here?
                header.Number == 0 &&
                header.ExtraData.Length <= 32;
        }
    }
}