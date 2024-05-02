// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Consensus.Messages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public class BlockValidator(
    ITxValidator? txValidator,
    IHeaderValidator? headerValidator,
    IUnclesValidator? unclesValidator,
    ISpecProvider? specProvider,
    ILogManager? logManager)
    : IBlockValidator
{
    private readonly IHeaderValidator _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
    private readonly ITxValidator _txValidator = txValidator ?? throw new ArgumentNullException(nameof(txValidator));
    private readonly IUnclesValidator _unclesValidator = unclesValidator ?? throw new ArgumentNullException(nameof(unclesValidator));
    private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle)
    {
        return _headerValidator.Validate(header, parent, isUncle, out _);
    }

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error)
    {
        return _headerValidator.Validate(header, parent, isUncle, out error);
    }

    public bool Validate(BlockHeader header, bool isUncle)
    {
        return _headerValidator.Validate(header, isUncle, out _);
    }

    public bool Validate(BlockHeader header, bool isUncle, out string? error)
    {
        return _headerValidator.Validate(header, isUncle, out error);
    }

    /// <summary>
    /// Applies to blocks without parent
    /// </summary>
    /// <param name="block">A block to validate</param>
    /// <param name="error">Error description in case of failed validation</param>
    /// <returns>Validation result</returns>
    /// <remarks>
    /// Parent may be absent during BeaconSync
    /// </remarks>
    public bool ValidateOrphanedBlock(Block block, out string? error)
    {
        if (!ValidateEip4844Fields(block, _specProvider.GetSpec(block.Header), out error))
            return false;

        error = null;
        return true;
    }

    /// <summary>
    /// Suggested block validation runs basic checks that can be executed before going through the expensive EVM processing.
    /// </summary>
    /// <param name="block">A block to validate</param>
    /// <returns>
    /// <c>true</c> if the <paramref name="block"/> is valid; otherwise, <c>false</c>.
    /// </returns>
    public bool ValidateSuggestedBlock(Block block)
    {
        return ValidateSuggestedBlock(block, out _);
    }

    /// <summary>
    /// Suggested block validation runs basic checks that can be executed before going through the expensive EVM processing.
    /// </summary>
    /// <param name="block">A block to validate</param>
    /// <param name="errorMessage">Message detailing a validation failure.</param>
    /// <param name="validateHashes"></param>
    /// <returns>
    /// <c>true</c> if the <paramref name="block"/> is valid; otherwise, <c>false</c>.
    /// </returns>
    public bool ValidateSuggestedBlock(Block block, out string? errorMessage, bool validateHashes = true)
    {
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        if (!ValidateTransactions(block, spec, out errorMessage))
            return false;

        if (!ValidateEip4844Fields(block, spec, out errorMessage))
            return false;

        if (spec.MaximumUncleCount < block.Uncles.Length)
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Uncle count of {block.Uncles.Length} exceeds the max limit of {spec.MaximumUncleCount}");
            errorMessage = BlockErrorMessages.ExceededUncleLimit(spec.MaximumUncleCount);
            return false;
        }

        if (validateHashes && !ValidateUnclesHashMatches(block, out Hash256 unclesHash))
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Uncles hash mismatch: expected {block.Header.UnclesHash}, got {unclesHash}");
            errorMessage = BlockErrorMessages.InvalidUnclesHash;
            return false;
        }

        if (block.Uncles.Length > 0 && !_unclesValidator.Validate(block.Header, block.Uncles))
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Invalid uncles");
            errorMessage = BlockErrorMessages.InvalidUncle;
            return false;
        }

        bool blockHeaderValid = _headerValidator.Validate(block.Header, false, out errorMessage);
        if (!blockHeaderValid)
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Invalid header");
            return false;
        }

        if (validateHashes)
        {
            if (!ValidateTxRootMatchesTxs(block, out Hash256 txRoot))
            {
                if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Transaction root hash mismatch: expected {block.Header.TxRoot}, got {txRoot}");
                errorMessage = BlockErrorMessages.InvalidTxRoot(block.Header.TxRoot!, txRoot);
                return false;
            }

            if (!ValidateWithdrawals(block, spec, out errorMessage))
            {
                return false;
            }

            if (!ValidateRequests(block, spec, out errorMessage))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Processed block validation is comparing the block hashes (which include all other results).
    /// We only make exact checks on what is invalid if the hash is different.
    /// </summary>
    /// <param name="processedBlock">This should be the block processing result (after going through the EVM processing)</param>
    /// <param name="receipts">List of tx receipts from the processed block (required only for better diagnostics when the receipt root is invalid).</param>
    /// <param name="suggestedBlock">Block received from the network - unchanged.</param>
    /// <returns><c>true</c> if the <paramref name="processedBlock"/> is valid; otherwise, <c>false</c>.</returns>
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
    {
        return ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out _);
    }

    /// <summary>
    /// Processed block validation is comparing the block hashes (which include all other results).
    /// We only make exact checks on what is invalid if the hash is different.
    /// </summary>
    /// <param name="processedBlock">This should be the block processing result (after going through the EVM processing)</param>
    /// <param name="receipts">List of tx receipts from the processed block (required only for better diagnostics when the receipt root is invalid).</param>
    /// <param name="suggestedBlock">Block received from the network - unchanged.</param>
    /// <param name="error">Detailed error message if validation fails otherwise <value>null</value>.</param>
    /// <returns><c>true</c> if the <paramref name="processedBlock"/> is valid; otherwise, <c>false</c>.</returns>
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, out string? error)
    {
        if (processedBlock.Header.Hash == suggestedBlock.Header.Hash)
        {
            error = null;
            return true;
        }

        if (_logger.IsWarn) _logger.Warn($"Processed block {processedBlock.ToString(Block.Format.Short)} is invalid:");
        if (_logger.IsWarn) _logger.Warn($"- hash: expected {suggestedBlock.Hash}, got {processedBlock.Hash}");
        error = null;
        if (processedBlock.Header.GasUsed != suggestedBlock.Header.GasUsed)
        {
            if (_logger.IsWarn) _logger.Warn($"- gas used: expected {suggestedBlock.Header.GasUsed}, got {processedBlock.Header.GasUsed} (diff: {processedBlock.Header.GasUsed - suggestedBlock.Header.GasUsed})");
            error ??= BlockErrorMessages.HeaderGasUsedMismatch;
        }

        if (processedBlock.Header.Bloom != suggestedBlock.Header.Bloom)
        {
            if (_logger.IsWarn) _logger.Warn($"- bloom: expected {suggestedBlock.Header.Bloom}, got {processedBlock.Header.Bloom}");
            error ??= BlockErrorMessages.InvalidLogsBloom;
        }

        if (processedBlock.Header.ReceiptsRoot != suggestedBlock.Header.ReceiptsRoot)
        {
            if (_logger.IsWarn) _logger.Warn($"- receipts root: expected {suggestedBlock.Header.ReceiptsRoot}, got {processedBlock.Header.ReceiptsRoot}");
            error ??= BlockErrorMessages.InvalidReceiptsRoot;
        }

        if (processedBlock.Header.StateRoot != suggestedBlock.Header.StateRoot)
        {
            if (_logger.IsWarn) _logger.Warn($"- state root: expected {suggestedBlock.Header.StateRoot}, got {processedBlock.Header.StateRoot}");
            error ??= BlockErrorMessages.InvalidStateRoot;
        }

        if (processedBlock.Header.BlobGasUsed != suggestedBlock.Header.BlobGasUsed)
        {
            if (_logger.IsWarn) _logger.Warn($"- blob gas used: expected {suggestedBlock.Header.BlobGasUsed}, got {processedBlock.Header.BlobGasUsed}");
            error ??= BlockErrorMessages.HeaderBlobGasMismatch;
        }

        if (processedBlock.Header.ExcessBlobGas != suggestedBlock.Header.ExcessBlobGas)
        {
            if (_logger.IsWarn) _logger.Warn($"- excess blob gas: expected {suggestedBlock.Header.ExcessBlobGas}, got {processedBlock.Header.ExcessBlobGas}");
            error ??= BlockErrorMessages.IncorrectExcessBlobGas;
        }

        if (processedBlock.Header.ParentBeaconBlockRoot != suggestedBlock.Header.ParentBeaconBlockRoot)
        {
            if (_logger.IsWarn) _logger.Warn($"- parent beacon block root : expected {suggestedBlock.Header.ParentBeaconBlockRoot}, got {processedBlock.Header.ParentBeaconBlockRoot}");
            error ??= BlockErrorMessages.InvalidParentBeaconBlockRoot;
        }

        if (processedBlock.Header.RequestsRoot != suggestedBlock.Header.RequestsRoot)
        {
            if (_logger.IsWarn) _logger.Warn($"- requests root : expected {suggestedBlock.Header.RequestsRoot}, got {processedBlock.Header.RequestsRoot}");
            error ??= BlockErrorMessages.InvalidRequestsRoot(suggestedBlock.Header.RequestsRoot, processedBlock.Header.RequestsRoot);
        }

        for (int i = 0; i < processedBlock.Transactions.Length; i++)
        {
            if (receipts[i].Error is not null && receipts[i].GasUsed == 0 && receipts[i].Error == "invalid")
            {
                if (_logger.IsWarn) _logger.Warn($"- invalid transaction {i}");
                error ??= BlockErrorMessages.InvalidTxInBlock(i);
            }
        }

        if (suggestedBlock.ExtraData is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"- block extra data : {suggestedBlock.ExtraData.ToHexString()}, UTF8: {Encoding.UTF8.GetString(suggestedBlock.ExtraData)}");
        }

        return false;
    }

    public bool ValidateWithdrawals(Block block, out string? error) =>
        ValidateWithdrawals(block, _specProvider.GetSpec(block.Header), out error);

    private bool ValidateWithdrawals(Block block, IReleaseSpec spec, out string? error)
    {
        if (spec.WithdrawalsEnabled && block.Withdrawals is null)
        {
            error = BlockErrorMessages.MissingWithdrawals;

            if (_logger.IsWarn) _logger.Warn($"Withdrawals cannot be null in block {block.Hash} when EIP-4895 activated.");

            return false;
        }

        if (!spec.WithdrawalsEnabled && block.Withdrawals is not null)
        {
            error = BlockErrorMessages.WithdrawalsNotEnabled;

            if (_logger.IsWarn) _logger.Warn($"Withdrawals must be null in block {block.Hash} when EIP-4895 not activated.");

            return false;
        }

        if (block.Withdrawals is not null)
        {
            if (!ValidateWithdrawalsHashMatches(block, out Hash256 withdrawalsRoot))
            {
                error = BlockErrorMessages.InvalidWithdrawalsRoot(block.Header.WithdrawalsRoot, withdrawalsRoot);
                if (_logger.IsWarn) _logger.Warn($"Withdrawals root hash mismatch in block {block.ToString(Block.Format.FullHashAndNumber)}: expected {block.Header.WithdrawalsRoot}, got {withdrawalsRoot}");

                return false;
            }
        }

        error = null;

        return true;
    }

    public bool ValidateRequests(Block block, out string? error) =>
        ValidateRequests(block, _specProvider.GetSpec(block.Header), out error);

    private bool ValidateRequests(Block block, IReleaseSpec spec, out string? error)
    {
        if (spec.ConsensusRequestsEnabled && block.Requests is null)
        {
            error = BlockErrorMessages.MissingRequests;

            if (_logger.IsWarn) _logger.Warn(error);

            return false;
        }

        if (!spec.ConsensusRequestsEnabled && block.Requests is not null)
        {
            error = BlockErrorMessages.RequestsNotEnabled;

            if (_logger.IsWarn) _logger.Warn(error);

            return false;
        }

        if (!ValidateRequestsHashMatches(block, out Hash256 depositsRoot))
        {
            error = BlockErrorMessages.InvalidRequestsRoot(block.Header.RequestsRoot, depositsRoot);
            if (_logger.IsWarn) _logger.Warn($"DepositsRoot root hash mismatch in block {block.ToString(Block.Format.FullHashAndNumber)}: expected {block.Header.RequestsRoot}, got {depositsRoot}");

            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateTransactions(Block block, IReleaseSpec spec, out string? errorMessage)
    {
        Transaction[] transactions = block.Transactions;

        for (int txIndex = 0; txIndex < transactions.Length; txIndex++)
        {
            Transaction transaction = transactions[txIndex];

            if (!_txValidator.IsWellFormed(transaction, spec, out errorMessage))
            {
                if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Invalid transaction: {errorMessage}");
                return false;
            }
        }
        errorMessage = null;
        return true;
    }

    private bool ValidateEip4844Fields(Block block, IReleaseSpec spec, out string? error)
    {
        if (!spec.IsEip4844Enabled)
        {
            error = null;
            return true;
        }

        int blobsInBlock = 0;
        UInt256 blobGasPrice = UInt256.Zero;
        Transaction[] transactions = block.Transactions;

        for (int txIndex = 0; txIndex < transactions.Length; txIndex++)
        {
            Transaction transaction = transactions[txIndex];

            if (!transaction.SupportsBlobs)
            {
                continue;
            }

            if (blobGasPrice.IsZero)
            {
                if (!BlobGasCalculator.TryCalculateBlobGasPricePerUnit(block.Header, out blobGasPrice))
                {
                    error = BlockErrorMessages.BlobGasPriceOverflow;
                    if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} {error}.");
                    return false;
                }
            }

            if (transaction.MaxFeePerBlobGas < blobGasPrice)
            {
                error = BlockErrorMessages.InsufficientMaxFeePerBlobGas;
                if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Transaction at index {txIndex} has insufficient {nameof(transaction.MaxFeePerBlobGas)} to cover current blob gas fee: {transaction.MaxFeePerBlobGas} < {blobGasPrice}.");
                return false;
            }

            blobsInBlock += transaction.BlobVersionedHashes!.Length;
        }

        ulong blobGasUsed = BlobGasCalculator.CalculateBlobGas(blobsInBlock);

        if (blobGasUsed > Eip4844Constants.MaxBlobGasPerBlock)
        {
            error = BlockErrorMessages.BlobGasUsedAboveBlockLimit;
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} {error}.");
            return false;
        }

        if (blobGasUsed != block.Header.BlobGasUsed)
        {
            error = BlockErrorMessages.HeaderBlobGasMismatch;
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} {nameof(BlockHeader.BlobGasUsed)} declared in the block header does not match actual blob gas used: {block.Header.BlobGasUsed} != {blobGasUsed}.");
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated) =>
        ValidateTxRootMatchesTxs(header, toBeValidated, out _)
        && ValidateUnclesHashMatches(header, toBeValidated, out _)
        && ValidateWithdrawalsHashMatches(header, toBeValidated, out _)
        && ValidateRequestsHashMatches(header, toBeValidated, out _);

    public static bool ValidateTxRootMatchesTxs(Block block, out Hash256 txRoot) =>
        ValidateTxRootMatchesTxs(block.Header, block.Body, out txRoot);

    public static bool ValidateTxRootMatchesTxs(BlockHeader header, BlockBody body, out Hash256 txRoot) =>
        (txRoot = TxTrie.CalculateRoot(body.Transactions)) == header.TxRoot;

    public static bool ValidateUnclesHashMatches(Block block, out Hash256 unclesHash) =>
        ValidateUnclesHashMatches(block.Header, block.Body, out unclesHash);

    public static bool ValidateUnclesHashMatches(BlockHeader header, BlockBody body, out Hash256 unclesHash) =>
        (unclesHash = UnclesHash.Calculate(body.Uncles)) == header.UnclesHash;

    public static bool ValidateWithdrawalsHashMatches(Block block, out Hash256? withdrawalsRoot) =>
        ValidateWithdrawalsHashMatches(block.Header, block.Body, out withdrawalsRoot);

    public static bool ValidateWithdrawalsHashMatches(BlockHeader header, BlockBody body, out Hash256? withdrawalsRoot)
    {
        // if one is null and other isn't
        if (body.Withdrawals is null ^ header.WithdrawalsRoot is null)
        {
            withdrawalsRoot = null;
            return false;
        }

        withdrawalsRoot = new WithdrawalTrie(body.Withdrawals).RootHash;
        return header.WithdrawalsRoot == withdrawalsRoot;
    }

    public static bool ValidateRequestsHashMatches(Block block, out Hash256? withdrawalsRoot) =>
        ValidateRequestsHashMatches(block.Header, block.Body, out withdrawalsRoot);

    public static bool ValidateRequestsHashMatches(BlockHeader header, BlockBody body, out Hash256? requestsRoot)
    {
        // if one is null and other isn't
        if (body.Requests is null ^ header.RequestsRoot is null)
        {
            requestsRoot = null;
            return false;
        }

        requestsRoot = new RequestsTrie(body.Requests).RootHash;
        return requestsRoot == header.RequestsRoot;
    }

    private static string Invalid(Block block) =>
        $"Invalid block {block.ToString(Block.Format.FullHashAndNumber)}:";
}
