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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Validators
{
    public class HeaderValidator : IHeaderValidator
    {
        private static readonly byte[] DaoExtraData = Bytes.FromHexString("0x64616f2d686172642d666f726b");

        private readonly ISealValidator _sealValidator;
        private readonly ISpecProvider _specProvider;
        private readonly long? _daoBlockNumber;
        protected readonly ILogger _logger;
        private readonly IBlockTree _blockTree;

        public HeaderValidator(
            IBlockTree? blockTree,
            ISealValidator? sealValidator,
            ISpecProvider? specProvider,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _daoBlockNumber = specProvider.DaoBlockNumber;
        }

        public virtual bool ValidateHash(BlockHeader header)
        {
            bool hashAsExpected = header.Hash == header.CalculateHash();
            if (!hashAsExpected)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - invalid block hash");
            }

            return hashAsExpected;
        }

        /// <summary>
        /// Note that this does not validate seal which is the responsibility of <see cref="ISealValidator"/>>
        /// </summary>
        /// <param name="header">BlockHeader to validate</param>
        /// <param name="parent">BlockHeader which is the parent of <paramref name="header"/></param>
        /// <param name="isUncle"><value>True</value> if uncle block, otherwise <value>False</value></param>
        /// <returns></returns>
        public virtual bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
        {
            bool hashAsExpected = ValidateHash(header);

            IReleaseSpec spec = _specProvider.GetSpec(header.Number);
            bool extraDataValid = ValidateExtraData(header, parent, spec, isUncle);
            if (parent == null)
            {
                if (header.Number == 0)
                {
                    bool isGenesisValid = ValidateGenesis(header);
                    if (!isGenesisValid)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Invalid genesis block header ({header.Hash})");
                    }

                    return isGenesisValid;
                }

                if (_logger.IsDebug)
                    _logger.Debug($"Orphan block, could not find parent ({header.ParentHash}) of ({header.Hash})");
                return false;
            }

            bool totalDifficultyCorrect = ValidateTotalDifficulty(parent, header);
            
            bool sealParamsCorrect = _sealValidator.ValidateParams(parent, header);
            if (!sealParamsCorrect)
            {
                 if (_logger.IsWarn)
                    _logger.Warn($"Invalid block header ({header.Hash}) - seal parameters incorrect"); 
            }

            bool gasUsedBelowLimit = header.GasUsed <= header.GasLimit;
            if (!gasUsedBelowLimit)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas used above gas limit");
            }

            bool gasLimitInRange = ValidateGasLimitRange(header, parent, spec);

            // bool gasLimitAboveAbsoluteMinimum = header.GasLimit >= 125000; // described in the YellowPaper but not followed

            bool timestampMoreThanAtParent = ValidateTimestamp(parent, header);

            bool numberIsParentPlusOne = header.Number == parent.Number + 1;
            if (!numberIsParentPlusOne)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Invalid block header ({header.Hash}) - block number is not parent + 1");
            }

            if (_logger.IsTrace)
                _logger.Trace(
                    $"Validating block {header.ToString(BlockHeader.Format.Short)}, extraData {header.ExtraData.ToHexString(true)}");

            bool eip1559Valid = true;
            bool isEip1559Enabled = spec.IsEip1559Enabled;
            if (isEip1559Enabled)
            {
                UInt256? expectedBaseFee = BaseFeeCalculator.Calculate(parent, spec);
                eip1559Valid = expectedBaseFee == header.BaseFeePerGas;
                
                if (expectedBaseFee != header.BaseFeePerGas)
                {
                    if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.ToString(BlockHeader.Format.Short)}) incorrect base fee. Expected base fee: {expectedBaseFee}, Current base fee: {header.BaseFeePerGas} ");
                    eip1559Valid = false;
                }
            }

            return
                totalDifficultyCorrect &&
                gasUsedBelowLimit &&
                gasLimitInRange &&
                sealParamsCorrect &&
                // gasLimitAboveAbsoluteMinimum && // described in the YellowPaper but not followed
                timestampMoreThanAtParent &&  
                numberIsParentPlusOne &&
                hashAsExpected &&
                extraDataValid &&
                eip1559Valid;
        }
        protected virtual bool ValidateExtraData(BlockHeader header, BlockHeader? parent, IReleaseSpec spec, bool isUncle = false)
        {
            bool extraDataValid =  header.ExtraData.Length <= spec.MaximumExtraDataSize
                                   && (isUncle
                                       || _daoBlockNumber == null
                                       || header.Number < _daoBlockNumber
                                       || header.Number >= _daoBlockNumber + 10
                                       || Bytes.AreEqual(header.ExtraData, DaoExtraData));
            if (!extraDataValid)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - DAO extra data not valid. MaximumExtraDataSize {spec.MaximumExtraDataSize}, ExtraDataLength {header.ExtraData.Length}, DaoBlockNumber: {_daoBlockNumber}");
            }
            return extraDataValid;
        }

        protected virtual bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec)
        {
            long adjustedParentGasLimit = Eip1559GasLimitAdjuster.AdjustGasLimit(spec, parent.GasLimit, header.Number);
            long maxGasLimitDifference = adjustedParentGasLimit / spec.GasLimitBoundDivisor;

            long maxNextGasLimit = adjustedParentGasLimit + maxGasLimitDifference;
            bool gasLimitNotTooHigh;
            bool notToHighWithOverflow = long.MaxValue - maxGasLimitDifference < adjustedParentGasLimit;
            if (notToHighWithOverflow)
            {
                // The edge case used in hive tests. If adjustedParentGasLimit + maxGasLimitDifference >=  long.MaxValue,
                // we can check for long.MaxValue - maxGasLimitDifference < adjustedParentGasLimit to ensure that we are in range.
                // In hive we have tests that using long.MaxValue in the genesis block
                // Even if we add maxGasLimitDifference we don't get header.GasLimit higher than long.MaxValue
                gasLimitNotTooHigh = true;
            }
            else
            {
                gasLimitNotTooHigh = header.GasLimit < maxNextGasLimit;
            }


            if (!gasLimitNotTooHigh)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas limit too high");
            }

            bool gasLimitNotTooLow = header.GasLimit > adjustedParentGasLimit - maxGasLimitDifference &&
                                     header.GasLimit >= spec.MinGasLimit;
            if (!gasLimitNotTooLow)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas limit too low");
            }

            return gasLimitNotTooHigh && gasLimitNotTooLow;
        }

        private bool ValidateTimestamp(BlockHeader parent,BlockHeader header)
        {
            bool timestampMoreThanAtParent = header.Timestamp > parent.Timestamp;
            if (!timestampMoreThanAtParent)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - timestamp before parent");
            }
            return timestampMoreThanAtParent;
        }
        
        protected virtual bool ValidateTotalDifficulty(BlockHeader parent, BlockHeader header)
        {
            bool totalDifficultyCorrect = true;
            if (header.TotalDifficulty != null)
            {
                if (parent.TotalDifficulty + header.Difficulty != header.TotalDifficulty)
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid block header ({header.Hash}) - incorrect total difficulty");
                    totalDifficultyCorrect = false;
                }
            }

            return totalDifficultyCorrect;
        }

        /// <summary>
        /// Validates all the header elements (usually in relation to parent). Difficulty calculation is validated in <see cref="ISealValidator"/>
        /// </summary>
        /// <param name="header">Block header to validate</param>
        /// <param name="isUncle"><value>True</value> if the <paramref name="header"/> is an uncle, otherwise <value>False</value></param>
        /// <returns><value>True</value> if <paramref name="header"/> is valid, otherwise <value>False</value></returns>
        public virtual bool Validate(BlockHeader header, bool isUncle = false)
        {
            BlockHeader parent = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return Validate(header, parent, isUncle);
        }

        private bool ValidateGenesis(BlockHeader header)
        {
            return
                header.GasUsed < header.GasLimit &&
                header.GasLimit > _specProvider.GenesisSpec.MinGasLimit &&
                header.Timestamp > 0 &&
                header.Number == 0 &&
                header.Bloom is not null &&
                header.ExtraData.Length <= _specProvider.GenesisSpec.MaximumExtraDataSize;
        }
    }
}
