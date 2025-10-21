// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Validators
{
    public class HeaderValidator(
        IBlockTree? blockTree,
        ISealValidator? sealValidator,
        ISpecProvider? specProvider,
        ILogManager? logManager)
        : IHeaderValidator
    {
        private static readonly byte[] DaoExtraData = Bytes.FromHexString("0x64616f2d686172642d666f726b");

        protected readonly ISealValidator _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
        protected readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly long? _daoBlockNumber = specProvider.DaoBlockNumber;
        protected readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

        public static bool ValidateHash(BlockHeader header, out Hash256 actualHash)
        {
            actualHash = header.CalculateHash();
            return header.Hash == actualHash;
        }

        public static bool ValidateHash(BlockHeader header) => ValidateHash(header, out _);

        private bool ValidateHash(BlockHeader header, ref string? error)
        {
            bool hashAsExpected = ValidateHash(header);

            if (!hashAsExpected)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - invalid block hash");
                error = BlockErrorMessages.InvalidHeaderHash(header.Hash!, header.CalculateHash());
                return false;
            }

            return true;
        }

        /// <summary>
        /// Note that this does not validate seal which is the responsibility of <see cref="ISealValidator"/>>
        /// </summary>
        /// <param name="header">BlockHeader to validate</param>
        /// <param name="parent">BlockHeader which is the parent of <paramref name="header"/></param>
        /// <param name="isUncle"><value>True</value> if is an uncle block, otherwise <value>False</value></param>
        /// <returns><value>True</value> if validation succeeds otherwise <value>false</value></returns>
        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle = false) =>
            Validate(header, parent, isUncle, out _);

        /// <summary>
        /// Note that this does not validate seal which is the responsibility of <see cref="ISealValidator"/>>
        /// </summary>
        /// <param name="header">BlockHeader to validate</param>
        /// <param name="parent">BlockHeader which is the parent of <paramref name="header"/></param>
        /// <param name="isUncle"><value>True</value> if is an uncle block, otherwise <value>False</value></param>
        /// <param name="error">Detailed error message if validation fails, otherwise <value>null</value>.</param>
        /// <returns><value>True</value> if validation succeeds otherwise <value>false</value></returns>
        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, out string? error) =>
            Validate<OffFlag>(header, parent, isUncle, out error);

        protected virtual bool Validate<TOrphaned>(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error) where TOrphaned : struct, IFlag
        {
            IReleaseSpec spec;
            error = null;
            bool orphaned = typeof(TOrphaned) == typeof(OnFlag);
            parent = orphaned ? null : parent!;

            // bool gasLimitAboveAbsoluteMinimum = header.GasLimit >= 125000; // described in the YellowPaper but not followed
            return ValidateFieldLimit(header, ref error)
                   && ValidateHash(header, ref error)
                   && ValidateExtraData(header, spec = _specProvider.GetSpec(header), isUncle, ref error)
                   && (orphaned || ValidateParent(header, parent, ref error))
                   && (orphaned || ValidateTotalDifficulty(header, parent, ref error))
                   && (orphaned || ValidateSeal(header, parent, isUncle, ref error))
                   && ValidateGasUsed(header, ref error)
                   && (orphaned || ValidateGasLimitRange(header, parent, spec, ref error))
                   && (orphaned || ValidateTimestamp(header, parent, ref error))
                   && (orphaned || ValidateBlockNumber(header, parent, ref error))
                   && (orphaned || Validate1559(header, parent, spec, ref error))
                   && (orphaned || ValidateBlobGasFields(header, parent, spec, ref error))
                   && ValidateRequestsHash(header, spec, ref error);
        }

        public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
            Validate<OnFlag>(header, null, false, out error);

        protected virtual bool ValidateRequestsHash(BlockHeader header, IReleaseSpec spec, ref string? error)
        {
            if (spec.RequestsEnabled)
            {
                if (header.RequestsHash is null)
                {
                    if (_logger.IsWarn) _logger.Warn("RequestsHash field is not set.");
                    error = BlockErrorMessages.MissingRequests;
                    return false;
                }
            }
            else
            {
                if (header.RequestsHash is not null)
                {
                    if (_logger.IsWarn) _logger.Warn("RequestsHash field should not have value.");
                    error = BlockErrorMessages.RequestsNotEnabled;
                    return false;
                }
            }
            return true;
        }

        protected virtual bool Validate1559(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error)
        {
            if (spec.IsEip1559Enabled)
            {
                UInt256? expectedBaseFee = BaseFeeCalculator.Calculate(parent, spec);

                if (expectedBaseFee != header.BaseFeePerGas)
                {
                    if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.ToString(BlockHeader.Format.Short)}) incorrect base fee. Expected base fee: {expectedBaseFee}, Current base fee: {header.BaseFeePerGas} ");
                    error = BlockErrorMessages.InvalidBaseFeePerGas(expectedBaseFee, header.BaseFeePerGas);
                    return false;
                }
            }

            return true;
        }

        protected virtual bool ValidateBlockNumber(BlockHeader header, BlockHeader parent, ref string? error)
        {
            if (header.Number != parent.Number + 1)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - block number is not parent + 1");
                error = BlockErrorMessages.InvalidBlockNumber;
                return false;
            }

            return true;
        }

        protected virtual bool ValidateGasUsed(BlockHeader header, ref string? error)
        {
            if (header.GasUsed > header.GasLimit)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas used above gas limit");
                error = BlockErrorMessages.ExceededGasLimit;
                return false;
            }

            return true;
        }

        protected virtual bool ValidateParent(BlockHeader header, BlockHeader? parent, ref string? error)
        {
            if (parent is null)
            {
                if (header.Number != 0)
                {
                    if (_logger.IsDebug) _logger.Debug($"Orphan block, could not find parent ({header.ParentHash}) of ({header.Hash})");
                    error = BlockErrorMessages.InvalidAncestor;
                    return false;
                }

                bool isGenesisValid = ValidateGenesis(header);
                if (!isGenesisValid)
                {
                    if (_logger.IsWarn) _logger.Warn($"Invalid genesis block header ({header.Hash})");
                    error = BlockErrorMessages.InvalidGenesisBlock;
                    return false;
                }
            }
            else if (parent.Hash != header.ParentHash)
            {
                error = BlockErrorMessages.MismatchedParent(header.Hash!, header.ParentHash!, parent.Hash!);
                return false;
            }

            return true;
        }

        protected virtual bool ValidateSeal(BlockHeader header, BlockHeader parent, bool isUncle, ref string? error)
        {
            bool result = _sealValidator.ValidateParams(parent, header, isUncle);

            if (!result)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - seal parameters incorrect");
                error = BlockErrorMessages.InvalidSealParameters;
            }

            return result;
        }

        protected virtual bool ValidateFieldLimit(BlockHeader blockHeader, ref string? error)
        {
            // Note, these are out of spec. Technically, there could be a block with field with very high value that is
            // valid when using ulong, but wrapped to negative value when using long. However, switching to ulong
            // at this point can cause other unexpected error. So we just won't support it for now.

            error = blockHeader switch
            {
                { Number: < 0 } => BlockErrorMessages.NegativeBlockNumber,
                { GasLimit: < 0 } => BlockErrorMessages.NegativeGasLimit,
                { GasUsed: < 0 } => BlockErrorMessages.NegativeGasUsed,
                _ => null
            };

            if (error is null) return true;

            if (_logger.IsWarn)
                _logger.Warn($"Invalid block header ({blockHeader.Hash}) - {blockHeader switch
                {
                    { Number: < 0 } => $"Block number is negative {blockHeader.Number}",
                    { GasLimit: < 0 } => $"Block GasLimit is negative {blockHeader.GasLimit}",
                    { GasUsed: < 0 } => $"Block GasUsed is negative {blockHeader.GasUsed}",
                    _ => error
                }}");

            return false;

        }

        protected virtual bool ValidateExtraData(BlockHeader header, IReleaseSpec spec, bool isUncle, ref string? error)
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
                error = BlockErrorMessages.InvalidExtraData;
            }

            return extraDataValid;
        }

        protected virtual bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error)
        {
            long adjustedParentGasLimit = Eip1559GasLimitAdjuster.AdjustGasLimit(spec, parent.GasLimit, header.Number);
            long maxGasLimitDifference = adjustedParentGasLimit / spec.GasLimitBoundDivisor;

            long maxNextGasLimit = adjustedParentGasLimit + maxGasLimitDifference;
            bool notToHighWithOverflow = long.MaxValue - maxGasLimitDifference < adjustedParentGasLimit;
            // The edge case used in hive tests. If adjustedParentGasLimit + maxGasLimitDifference >=  long.MaxValue,
            // we can check for long.MaxValue - maxGasLimitDifference < adjustedParentGasLimit to ensure that we are in range.
            // In hive we have tests that using long.MaxValue in the genesis block
            // Even if we add maxGasLimitDifference we don't get header.GasLimit higher than long.MaxValue
            var gasLimitNotTooHigh = notToHighWithOverflow || header.GasLimit < maxNextGasLimit;

            if (!gasLimitNotTooHigh)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas limit too high");
                error = BlockErrorMessages.InvalidGasLimit;
            }

            bool gasLimitNotTooLow = header.GasLimit > adjustedParentGasLimit - maxGasLimitDifference &&
                                     header.GasLimit >= spec.MinGasLimit;
            if (!gasLimitNotTooLow)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas limit too low. " +
                                                 $"Gas limit: {header.GasLimit}, " +
                                                 $"Adjusted parent gas limit: {adjustedParentGasLimit}, " +
                                                 $"Max gas limit difference: {maxGasLimitDifference}, " +
                                                 $"Spec min gas limit: {spec.MinGasLimit}");
                error = BlockErrorMessages.InvalidGasLimit;
            }

            return gasLimitNotTooHigh && gasLimitNotTooLow;
        }

        protected virtual bool ValidateTimestamp(BlockHeader header, BlockHeader parent, ref string? error)
        {
            bool timestampMoreThanAtParent = header.Timestamp > parent.Timestamp;
            if (!timestampMoreThanAtParent)
            {
                error = BlockErrorMessages.InvalidTimestamp;
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - timestamp before parent");
            }
            return timestampMoreThanAtParent;
        }

        protected virtual bool ValidateTotalDifficulty(BlockHeader header, BlockHeader parent, ref string? error)
        {
            bool result = true;

            if (header.TotalDifficulty is not null)
            {
                if (header.TotalDifficulty == 0)
                {
                    // Same as in BlockTree.SetTotalDifficulty
                    if (!(_blockTree.Genesis!.Difficulty == 0 && _specProvider.TerminalTotalDifficulty == 0))
                    {
                        if (_logger.IsDebug) _logger.Debug($"Invalid block header ({header.Hash}) - zero total difficulty when genesis or ttd is not zero");
                        result = false;
                        error = BlockErrorMessages.InvalidTotalDifficulty;
                    }
                }
                else if (parent.TotalDifficulty + header.Difficulty != header.TotalDifficulty)
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid block header ({header.Hash}) - incorrect total difficulty");
                    result = false;
                    error = BlockErrorMessages.InvalidTotalDifficulty;
                }
            }

            return result;
        }

        private bool ValidateGenesis(BlockHeader header) =>
            header.GasUsed < header.GasLimit &&
            header.GasLimit > _specProvider.GenesisSpec.MinGasLimit &&
            header.Timestamp > 0 &&
            header.Number == 0 &&
            header.Bloom is not null &&
            header.ExtraData.Length <= _specProvider.GenesisSpec.MaximumExtraDataSize;

        protected virtual bool ValidateBlobGasFields(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error)
        {
            if (spec.IsEip4844Enabled)
            {
                if (header.BlobGasUsed is null)
                {
                    if (_logger.IsWarn) _logger.Warn("BlobGasUsed field is not set.");
                    error = BlockErrorMessages.MissingBlobGasUsed;
                    return false;
                }

                if (header.ExcessBlobGas is null)
                {
                    if (_logger.IsWarn) _logger.Warn("ExcessBlobGas field is not set.");
                    error = BlockErrorMessages.MissingExcessBlobGas;
                    return false;
                }

                ulong? expectedExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
                if (header.ExcessBlobGas != expectedExcessBlobGas)
                {
                    if (_logger.IsWarn) _logger.Warn($"ExcessBlobGas field is incorrect: {header.ExcessBlobGas}, should be {expectedExcessBlobGas}.");
                    error = BlockErrorMessages.IncorrectExcessBlobGas(expectedExcessBlobGas, header.ExcessBlobGas);
                    return false;
                }
            }
            else
            {
                if (header.BlobGasUsed is not null)
                {
                    if (_logger.IsWarn) _logger.Warn("BlobGasUsed field should not have value.");
                    error = BlockErrorMessages.NotAllowedBlobGasUsed;
                    return false;
                }

                if (header.ExcessBlobGas is not null)
                {
                    if (_logger.IsWarn) _logger.Warn("ExcessBlobGas field should not have value.");
                    error = BlockErrorMessages.NotAllowedExcessBlobGas;
                    return false;
                }
            }

            return true;
        }
    }
}
