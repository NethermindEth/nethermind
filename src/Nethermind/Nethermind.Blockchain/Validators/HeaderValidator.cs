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
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Validators
{
    public class HeaderValidator : IHeaderValidator
    {
        private static readonly byte[] DaoExtraData = Bytes.FromHexString("0x64616f2d686172642d666f726b");

        private readonly ISealValidator _sealValidator;
        private readonly ISpecProvider _specProvider;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly long? _daoBlockNumber;
        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;

        public HeaderValidator(IBlockTree? blockTree, ISealValidator? sealValidator, ISpecProvider? specProvider, IPoSSwitcher poSSwitcher, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));;
            _daoBlockNumber = specProvider.DaoBlockNumber;
        }

        public bool ValidateHash(BlockHeader header)
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
        public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
        {
            bool hashAsExpected = ValidateHash(header);

            IReleaseSpec spec = _specProvider.GetSpec(header.Number);
            bool extraDataValid = header.ExtraData.Length <= spec.MaximumExtraDataSize
                                  && (isUncle
                                      || _daoBlockNumber == null
                                      || header.Number < _daoBlockNumber
                                      || header.Number >= _daoBlockNumber + 10
                                      || Bytes.AreEqual(header.ExtraData, DaoExtraData));
            if (!extraDataValid)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - DAO extra data not valid");
            }

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

                if (_logger.IsDebug) _logger.Debug($"Orphan block, could not find parent ({header.ParentHash}) of ({header.Hash})");
                return false;
            }

            bool totalDifficultyCorrect = true;
            if (_poSSwitcher.IsPos(header) == false && header.TotalDifficulty != null)
            {
                if (parent.TotalDifficulty + header.Difficulty != header.TotalDifficulty)
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid total difficulty");
                    totalDifficultyCorrect = false;
                }
            }

            // seal is validated when synchronizing so we can remove it from here - review and test
            bool sealParamsCorrect = true;
            if (_poSSwitcher.IsPos(header, false) == false)
            {
                sealParamsCorrect = _sealValidator.ValidateParams(parent, header);
                if (!sealParamsCorrect)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"Invalid block header ({header.Hash}) - seal parameters incorrect");
                }
            }

            bool gasUsedBelowLimit = header.GasUsed <= header.GasLimit;
            if (!gasUsedBelowLimit)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas used above gas limit");
            }

            bool gasLimitInRange = ValidateGasLimitRange(header, parent, spec);

            // bool gasLimitAboveAbsoluteMinimum = header.GasLimit >= 125000; // described in the YellowPaper but not followed
            bool timestampMoreThanAtParent = header.Timestamp > parent.Timestamp;
            if (!timestampMoreThanAtParent)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - timestamp before parent");
            }

            bool numberIsParentPlusOne = header.Number == parent.Number + 1;
            if (!numberIsParentPlusOne)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - block number is not parent + 1");
            }

            if (_logger.IsTrace) _logger.Trace($"Validating block {header.ToString(BlockHeader.Format.Short)}, extraData {header.ExtraData.ToHexString(true)}");

            bool eip1559Valid = Validate1559Checks(header, parent, spec);
            bool theMergeValid = ValidateTheMergeChecks(header);

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
                eip1559Valid &&
                theMergeValid;
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

            bool gasLimitNotTooLow = header.GasLimit > adjustedParentGasLimit - maxGasLimitDifference && header.GasLimit >= spec.MinGasLimit;
            if (!gasLimitNotTooLow)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas limit too low");
            }

            return gasLimitNotTooHigh && gasLimitNotTooLow;
        }

        /// <summary>
        /// Validates all the header elements (usually in relation to parent). Difficulty calculation is validated in <see cref="ISealValidator"/>
        /// </summary>
        /// <param name="header">Block header to validate</param>
        /// <param name="isUncle"><value>True</value> if the <paramref name="header"/> is an uncle, otherwise <value>False</value></param>
        /// <returns><value>True</value> if <paramref name="header"/> is valid, otherwise <value>False</value></returns>
        public bool Validate(BlockHeader header, bool isUncle = false)
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

        private bool Validate1559Checks(BlockHeader header, BlockHeader parent, IReleaseSpec spec)
        {
            if (spec.IsEip1559Enabled == false)
                return true; 
            
            UInt256? expectedBaseFee = BaseFeeCalculator.Calculate(parent, spec);
            bool isBaseFeeCorrect = ValidateHeaderField(header, header.BaseFeePerGas, expectedBaseFee, nameof(header.BaseFeePerGas));
            return isBaseFeeCorrect;
        }

        private bool ValidateTheMergeChecks(BlockHeader header)
        {
            if (_poSSwitcher.IsPos(header) == false)
                return true;
            
            bool validDifficulty = ValidateHeaderField(header, header.Difficulty, UInt256.Zero, nameof(header.Difficulty));
            bool validNonce = ValidateHeaderField(header, header.Nonce, 0ul, nameof(header.Nonce));
            // validExtraData needed in previous version of EIP-3675 specification
            //bool validExtraData = ValidateHeaderField<byte>(header, header.ExtraData, Array.Empty<byte>(), nameof(header.ExtraData));
            bool validMixHash = ValidateHeaderField(header, header.MixHash, Keccak.Zero, nameof(header.MixHash));
            bool validUncles = ValidateHeaderField(header, header.UnclesHash, Keccak.OfAnEmptySequenceRlp, nameof(header.UnclesHash));
            
            return validDifficulty
                   && validNonce
                   //&& validExtraData
                   && validMixHash
                   && validUncles;

        }
        private bool ValidateHeaderField<T>(BlockHeader header, T value, T expected, string name)
        {
            if (Equals(value, expected)) return true;
            if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - the {name} is incorrect expected {expected}, got {value} .");
            return false;

        }
        
        private bool ValidateHeaderField<T>(BlockHeader request, IEnumerable<T>? value, IEnumerable<T> expected, string name)
        {
            if ((value ?? Array.Empty<T>()).SequenceEqual(expected)) return true;
            if (_logger.IsWarn) _logger.Warn($"Block {request} has invalid {name}, expected {expected}, got {value} .");
            return false;

        }
    }
    
    
}
