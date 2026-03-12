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
        protected readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

        /// <summary>
        /// Logs a warning (when enabled) and sets the error, returning <c>false</c>.
        /// Use for constant or pre-computed warn messages where the string is already allocated.
        /// </summary>
        protected bool WarnFail(ref string? error, string errorMessage, string warnMessage)
        {
            if (_logger.IsWarn) _logger.Warn(warnMessage);
            error = errorMessage;
            return false;
        }

        /// <summary>
        /// Logs the error message itself as a warning (when enabled) and returns <c>false</c>.
        /// Use when the error message is descriptive enough to serve as the log message.
        /// </summary>
        protected bool WarnFail(ref string? error, string errorMessage)
        {
            if (_logger.IsWarn) _logger.Warn(errorMessage);
            error = errorMessage;
            return false;
        }

        public static bool ValidateHash(BlockHeader header, out Hash256 actualHash)
        {
            actualHash = header.CalculateHash();
            return header.Hash == actualHash;
        }

        public static bool ValidateHash(BlockHeader header) => ValidateHash(header, out _);

        private bool ValidateHash(BlockHeader header, ref string? error)
        {
            if (ValidateHash(header))
                return true;

            return WarnFail(ref error,
                BlockErrorMessages.InvalidHeaderHash(header.Hash!, header.CalculateHash()),
                $"Invalid block header ({header.Hash}) - invalid block hash");
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

#if !ZK_EVM
        virtual
#endif
        protected bool Validate<TOrphaned>(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error) where TOrphaned : struct, IFlag
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
                   && ValidateRequestsHash(header, spec, ref error)
                   && ValidateBlockAccessListHash(header, spec, ref error)
                   && (orphaned || ValidateSlotNumber(header, parent, spec, ref error));
        }

        public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
            Validate<OnFlag>(header, null, false, out error);

        protected virtual bool ValidateRequestsHash(BlockHeader header, IReleaseSpec spec, ref string? error)
        {
            if (spec.RequestsEnabled)
            {
                if (header.RequestsHash is null)
                    return WarnFail(ref error, BlockErrorMessages.MissingRequests, "RequestsHash field is not set.");
            }
            else
            {
                if (header.RequestsHash is not null)
                    return WarnFail(ref error, BlockErrorMessages.RequestsNotEnabled, "RequestsHash field should not have value.");
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
                    // Error message is always allocated, so the warn interpolation is acceptable
                    return WarnFail(ref error,
                        BlockErrorMessages.InvalidBaseFeePerGas(expectedBaseFee, header.BaseFeePerGas),
                        $"Invalid block header ({header.ToString(BlockHeader.Format.Short)}) incorrect base fee. Expected base fee: {expectedBaseFee}, Current base fee: {header.BaseFeePerGas} ");
                }
            }

            return true;
        }

        protected virtual bool ValidateBlockNumber(BlockHeader header, BlockHeader parent, ref string? error)
        {
            if (header.Number != parent.Number + 1)
                return WarnFail(ref error, BlockErrorMessages.InvalidBlockNumber,
                    $"Invalid block header ({header.Hash}) - block number is not parent + 1");

            return true;
        }

        protected virtual bool ValidateGasUsed(BlockHeader header, ref string? error)
        {
            if (header.GasUsed > header.GasLimit)
                return WarnFail(ref error, BlockErrorMessages.ExceededGasLimit,
                    $"Invalid block header ({header.Hash}) - gas used above gas limit");

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

                if (!ValidateGenesis(header))
                    return WarnFail(ref error, BlockErrorMessages.InvalidGenesisBlock,
                        $"Invalid genesis block header ({header.Hash})");
            }
            else if (parent.Hash != header.ParentHash)
            {
                error = BlockErrorMessages.MismatchedParent(header.Hash!, header.ParentHash!, parent.Hash!);
                parent.SlotNumber = 0;
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
            bool gasLimitNotTooHigh = notToHighWithOverflow || header.GasLimit < maxNextGasLimit;

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
            if (header.Timestamp > parent.Timestamp)
                return true;

            return WarnFail(ref error, BlockErrorMessages.InvalidTimestamp,
                $"Invalid block header ({header.Hash}) - timestamp before parent");
        }

        protected virtual bool ValidateTotalDifficulty(BlockHeader header, BlockHeader parent, ref string? error)
        {
            if (header.TotalDifficulty is null)
                return true;

            if (header.TotalDifficulty == 0)
            {
                // Same as in BlockTree.SetTotalDifficulty
                if (!(_blockTree.Genesis!.Difficulty == 0 && _specProvider.TerminalTotalDifficulty == 0))
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid block header ({header.Hash}) - zero total difficulty when genesis or ttd is not zero");
                    error = BlockErrorMessages.InvalidTotalDifficulty;
                    return false;
                }
            }
            else if (parent.TotalDifficulty + header.Difficulty != header.TotalDifficulty)
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid block header ({header.Hash}) - incorrect total difficulty");
                error = BlockErrorMessages.InvalidTotalDifficulty;
                return false;
            }

            return true;
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
                    return WarnFail(ref error, BlockErrorMessages.MissingBlobGasUsed, "BlobGasUsed field is not set.");

                if (header.ExcessBlobGas is null)
                    return WarnFail(ref error, BlockErrorMessages.MissingExcessBlobGas, "ExcessBlobGas field is not set.");

                ulong? expectedExcessBlobGas = CalculateExcessBlobGas(parent, spec);
                if (header.ExcessBlobGas != expectedExcessBlobGas)
                {
                    // Error message is always allocated, so the warn interpolation is acceptable
                    return WarnFail(ref error,
                        BlockErrorMessages.IncorrectExcessBlobGas(expectedExcessBlobGas, header.ExcessBlobGas),
                        $"ExcessBlobGas field is incorrect: {header.ExcessBlobGas}, should be {expectedExcessBlobGas}.");
                }
            }
            else
            {
                if (header.BlobGasUsed is not null)
                    return WarnFail(ref error, BlockErrorMessages.NotAllowedBlobGasUsed, "BlobGasUsed field should not have value.");

                if (header.ExcessBlobGas is not null)
                    return WarnFail(ref error, BlockErrorMessages.NotAllowedExcessBlobGas, "ExcessBlobGas field should not have value.");
            }

            return true;
        }

        protected virtual ulong? CalculateExcessBlobGas(BlockHeader parent, IReleaseSpec spec)
        {
            return BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
        }

        private bool ValidateBlockAccessListHash(BlockHeader header, IReleaseSpec spec, ref string? error)
        {
            if (spec.IsEip7928Enabled)
            {
                if (header.BlockAccessListHash is null)
                    return WarnFail(ref error, BlockErrorMessages.MissingBlockLevelAccessListHash, "BlockAccessListHash field is not set.");
            }
            else
            {
                if (header.BlockAccessListHash is not null)
                    return WarnFail(ref error, BlockErrorMessages.BlockLevelAccessListHashNotEnabled, "BlockAccessListHash field should not have value.");
            }
            return true;
        }

        protected virtual bool ValidateSlotNumber(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error)
        {
            if (spec.IsEip7843Enabled)
            {
                if (header.SlotNumber is null)
                    return WarnFail(ref error, BlockErrorMessages.MissingSlotNumber, "SlotNumber field is not set.");

                if (parent.SlotNumber is not null && parent.SlotNumber != 0 && header.SlotNumber <= parent.SlotNumber)
                    return WarnFail(ref error, BlockErrorMessages.InvalidSlotNumber,
                        $"Invalid slot number ({header.SlotNumber}) - slot number must exceed parent ({parent.SlotNumber})");
            }
            else
            {
                if (header.SlotNumber is not null)
                    return WarnFail(ref error, BlockErrorMessages.SlotNumberNotEnabled, "SlotNumber field should not have value.");
            }

            return true;
        }

    }
}
