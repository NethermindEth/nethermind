// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
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

        public static bool ValidateHash(BlockHeader header) => header.Hash == header.CalculateHash();

        /// <summary>
        /// Note that this does not validate seal which is the responsibility of <see cref="ISealValidator"/>>
        /// </summary>
        /// <param name="header">BlockHeader to validate</param>
        /// <param name="parent">BlockHeader which is the parent of <paramref name="header"/></param>
        /// <param name="isUncle"><value>True</value> if uncle block, otherwise <value>False</value></param>
        /// <returns></returns>
        public virtual bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
        {
            if (!ValidateFieldLimit(header))
            {
                return false;
            }

            bool hashAsExpected = ValidateHash(header);

            if (!hashAsExpected)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - invalid block hash");
            }

            IReleaseSpec spec = _specProvider.GetSpec(header);
            bool extraDataValid = ValidateExtraData(header, parent, spec, isUncle);
            if (parent is null)
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

            bool totalDifficultyCorrect = ValidateTotalDifficulty(parent, header);

            bool sealParamsCorrect = _sealValidator.ValidateParams(parent, header, isUncle);
            if (!sealParamsCorrect)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - seal parameters incorrect");
            }

            bool gasUsedBelowLimit = header.GasUsed <= header.GasLimit;
            if (!gasUsedBelowLimit)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas used above gas limit");
            }

            bool gasLimitInRange = ValidateGasLimitRange(header, parent, spec);

            // bool gasLimitAboveAbsoluteMinimum = header.GasLimit >= 125000; // described in the YellowPaper but not followed

            bool timestampValid = ValidateTimestamp(parent, header);

            bool numberIsParentPlusOne = header.Number == parent.Number + 1;
            if (!numberIsParentPlusOne)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - block number is not parent + 1");
            }

            if (_logger.IsTrace) _logger.Trace($"Validating block {header.ToString(BlockHeader.Format.Short)}, extraData {header.ExtraData.ToHexString(true)}");

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

            bool eip4844Valid = ValidateBlobGasFields(header, parent, spec);

            return
                totalDifficultyCorrect &&
                gasUsedBelowLimit &&
                gasLimitInRange &&
                sealParamsCorrect &&
                // gasLimitAboveAbsoluteMinimum && // described in the YellowPaper but not followed
                timestampValid &&
                numberIsParentPlusOne &&
                hashAsExpected &&
                extraDataValid &&
                eip1559Valid &&
                eip4844Valid;
        }

        private bool ValidateFieldLimit(BlockHeader blockHeader)
        {
            // Note, these are out of spec. Technically, there could be a block with field with very high value that is
            // valid when using ulong, but wrapped to negative value when using long. However, switching to ulong
            // at this point can cause other unexpected error. So we just won't support it for now.
            if (blockHeader.Number < 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({blockHeader.Hash}) - Block number is negative {blockHeader.Number}");
                return false;
            }

            if (blockHeader.GasLimit < 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({blockHeader.Hash}) - Block GasLimit is negative {blockHeader.GasLimit}");
                return false;
            }

            if (blockHeader.GasUsed < 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({blockHeader.Hash}) - Block GasUsed is negative {blockHeader.GasUsed}");
                return false;
            }

            return true;
        }

        protected virtual bool ValidateExtraData(BlockHeader header, BlockHeader? parent, IReleaseSpec spec, bool isUncle = false)
        {
            bool extraDataValid = header.ExtraData.Length <= spec.MaximumExtraDataSize
                                   && (isUncle
                                       || _daoBlockNumber is null
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

        private bool ValidateTimestamp(BlockHeader parent, BlockHeader header)
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
            if (header.TotalDifficulty is null)
            {
                return true;
            }

            bool totalDifficultyCorrect = true;
            if (header.TotalDifficulty == 0)
            {
                // Same as in BlockTree.SetTotalDifficulty
                // TODO: Optimism validate td
                if (!(_blockTree.Genesis!.Difficulty == 0 && _specProvider.TerminalTotalDifficulty == 0) && (_specProvider.ChainId != 420 && _specProvider.ChainId != 10))
                {
                    if (_logger.IsDebug)
                        _logger.Debug($"Invalid block header ({header.Hash}) - zero total difficulty when genesis or ttd is not zero");
                    totalDifficultyCorrect = false;
                }
            }
            else
            {
                if (parent.TotalDifficulty + header.Difficulty != header.TotalDifficulty)
                {
                    if (_logger.IsDebug)
                        _logger.Debug($"Invalid block header ({header.Hash}) - incorrect total difficulty");
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

        private bool ValidateBlobGasFields(BlockHeader header, BlockHeader parentHeader, IReleaseSpec spec)
        {
            if (!spec.IsEip4844Enabled)
            {
                if (header.BlobGasUsed is not null)
                {
                    if (_logger.IsWarn) _logger.Warn($"BlobGasUsed field should not have value.");
                    return false;
                }

                if (header.ExcessBlobGas is not null)
                {
                    if (_logger.IsWarn) _logger.Warn($"ExcessBlobGas field should not have value.");
                    return false;
                }

                return true;
            }

            if (header.BlobGasUsed is null)
            {
                if (_logger.IsWarn) _logger.Warn($"BlobGasUsed field is not set.");
                return false;
            }

            if (header.ExcessBlobGas is null)
            {
                if (_logger.IsWarn) _logger.Warn($"ExcessBlobGas field is not set.");
                return false;
            }

            UInt256? expectedExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec);

            if (header.ExcessBlobGas != expectedExcessBlobGas)
            {
                if (_logger.IsWarn) _logger.Warn($"ExcessBlobGas field is incorrect: {header.ExcessBlobGas}, should be {expectedExcessBlobGas}.");
                return false;
            }
            return true;
        }
    }
}
