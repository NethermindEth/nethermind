// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public class BlockValidator : IBlockValidator
{
    private readonly IHeaderValidator _headerValidator;
    private readonly ITxValidator _txValidator;
    private readonly IUnclesValidator _unclesValidator;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public BlockValidator(
        ITxValidator? txValidator,
        IHeaderValidator? headerValidator,
        IUnclesValidator? unclesValidator,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _txValidator = txValidator ?? throw new ArgumentNullException(nameof(txValidator));
        _unclesValidator = unclesValidator ?? throw new ArgumentNullException(nameof(unclesValidator));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
    }

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle)
    {
        return _headerValidator.Validate(header, parent, isUncle);
    }

    public bool Validate(BlockHeader header, bool isUncle)
    {
        return _headerValidator.Validate(header, isUncle);
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
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        if (!ValidateTransactions(block, spec))
            return false;

        if (!ValidateEip4844Fields(block, spec, out _))
            return false;

        if (spec.MaximumUncleCount < block.Uncles.Length)
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Uncle count of {block.Uncles.Length} exceeds the max limit of {spec.MaximumUncleCount}");
            return false;
        }

        if (!ValidateUnclesHashMatches(block, out Hash256 unclesHash))
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Uncles hash mismatch: expected {block.Header.UnclesHash}, got {unclesHash}");
            return false;
        }

        if (!_unclesValidator.Validate(block.Header, block.Uncles))
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Invalid uncles");
            return false;
        }

        bool blockHeaderValid = _headerValidator.Validate(block.Header);
        if (!blockHeaderValid)
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Invalid header");
            return false;
        }

        if (!ValidateTxRootMatchesTxs(block, out Hash256 txRoot))
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Transaction root hash mismatch: expected {block.Header.TxRoot}, got {txRoot}");
            return false;
        }

        if (!ValidateWithdrawals(block, spec, out _))
            return false;

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
        bool isValid = processedBlock.Header.Hash == suggestedBlock.Header.Hash;
        if (!isValid && _logger.IsError)
        {
            _logger.Error($"Processed block {processedBlock.ToString(Block.Format.Short)} is invalid:");
            _logger.Error($"- hash: expected {suggestedBlock.Hash}, got {processedBlock.Hash}");

            if (processedBlock.Header.GasUsed != suggestedBlock.Header.GasUsed)
            {
                _logger.Error($"- gas used: expected {suggestedBlock.Header.GasUsed}, got {processedBlock.Header.GasUsed} (diff: {processedBlock.Header.GasUsed - suggestedBlock.Header.GasUsed})");
            }

            if (processedBlock.Header.Bloom != suggestedBlock.Header.Bloom)
            {
                _logger.Error($"- bloom: expected {suggestedBlock.Header.Bloom}, got {processedBlock.Header.Bloom}");
            }

            if (processedBlock.Header.ReceiptsRoot != suggestedBlock.Header.ReceiptsRoot)
            {
                _logger.Error($"- receipts root: expected {suggestedBlock.Header.ReceiptsRoot}, got {processedBlock.Header.ReceiptsRoot}");
            }

            if (processedBlock.Header.StateRoot != suggestedBlock.Header.StateRoot)
            {
                _logger.Error($"- state root: expected {suggestedBlock.Header.StateRoot}, got {processedBlock.Header.StateRoot}");
            }

            if (processedBlock.Header.BlobGasUsed != suggestedBlock.Header.BlobGasUsed)
            {
                _logger.Error($"- blob gas used: expected {suggestedBlock.Header.BlobGasUsed}, got {processedBlock.Header.BlobGasUsed}");
            }

            if (processedBlock.Header.ExcessBlobGas != suggestedBlock.Header.ExcessBlobGas)
            {
                _logger.Error($"- excess blob gas: expected {suggestedBlock.Header.ExcessBlobGas}, got {processedBlock.Header.ExcessBlobGas}");
            }

            if (processedBlock.Header.ParentBeaconBlockRoot != suggestedBlock.Header.ParentBeaconBlockRoot)
            {
                _logger.Error($"- parent beacon block root : expected {suggestedBlock.Header.ParentBeaconBlockRoot}, got {processedBlock.Header.ParentBeaconBlockRoot}");
            }

            for (int i = 0; i < processedBlock.Transactions.Length; i++)
            {
                if (receipts[i].Error is not null && receipts[i].GasUsed == 0 && receipts[i].Error == "invalid")
                {
                    _logger.Error($"- invalid transaction {i}");
                }
            }

            if (suggestedBlock.ExtraData is not null)
            {
                _logger.Error($"- block extra data : {suggestedBlock.ExtraData.ToHexString()}, UTF8: {Encoding.UTF8.GetString(suggestedBlock.ExtraData)}");
            }
        }

        return isValid;
    }

    public bool ValidateWithdrawals(Block block, out string? error) =>
        ValidateWithdrawals(block, _specProvider.GetSpec(block.Header), out error);

    private bool ValidateWithdrawals(Block block, IReleaseSpec spec, out string? error)
    {
        if (spec.WithdrawalsEnabled && block.Withdrawals is null)
        {
            error = $"Withdrawals cannot be null in block {block.Hash} when EIP-4895 activated.";

            if (_logger.IsWarn) _logger.Warn(error);

            return false;
        }

        if (!spec.WithdrawalsEnabled && block.Withdrawals is not null)
        {
            error = $"Withdrawals must be null in block {block.Hash} when EIP-4895 not activated.";

            if (_logger.IsWarn) _logger.Warn(error);

            return false;
        }

        if (block.Withdrawals is not null)
        {
            if (!ValidateWithdrawalsHashMatches(block, out Hash256 withdrawalsRoot))
            {
                error = $"Withdrawals root hash mismatch in block {block.ToString(Block.Format.FullHashAndNumber)}: expected {block.Header.WithdrawalsRoot}, got {withdrawalsRoot}";
                if (_logger.IsWarn) _logger.Warn($"Withdrawals root hash mismatch in block {block.ToString(Block.Format.FullHashAndNumber)}: expected {block.Header.WithdrawalsRoot}, got {withdrawalsRoot}");

                return false;
            }
        }

        error = null;

        return true;
    }

    private bool ValidateTransactions(Block block, IReleaseSpec spec)
    {
        Transaction[] transactions = block.Transactions;

        for (int txIndex = 0; txIndex < transactions.Length; txIndex++)
        {
            Transaction transaction = transactions[txIndex];

            if (!_txValidator.IsWellFormed(transaction, spec))
            {
                if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Invalid transaction {transaction.Hash}");
                return false;
            }
        }

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
                    error = "{nameof(blobGasPrice)} overflow";
                    if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} {error}.");
                    return false;
                }
            }

            if (transaction.MaxFeePerBlobGas < blobGasPrice)
            {
                error = $"A transaction has unsufficient {nameof(transaction.MaxFeePerBlobGas)} to cover current blob gas fee: {transaction.MaxFeePerBlobGas} < {blobGasPrice}";
                if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} {error}.");
                return false;
            }

            blobsInBlock += transaction.BlobVersionedHashes!.Length;
        }

        ulong blobGasUsed = BlobGasCalculator.CalculateBlobGas(blobsInBlock);

        if (blobGasUsed > Eip4844Constants.MaxBlobGasPerBlock)
        {
            error = $"A block cannot have more than {Eip4844Constants.MaxBlobGasPerBlock} blob gas.";
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} {error}.");
            return false;
        }

        if (blobGasUsed != block.Header.BlobGasUsed)
        {
            error = $"{Invalid(block)} {nameof(BlockHeader.BlobGasUsed)} declared in the block header does not match actual blob gas used: {block.Header.BlobGasUsed} != {blobGasUsed}.";
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} {error}.");
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated) =>
        ValidateTxRootMatchesTxs(header, toBeValidated, out _) &&
            ValidateUnclesHashMatches(header, toBeValidated, out _) &&
            ValidateWithdrawalsHashMatches(header, toBeValidated, out _);

    public static bool ValidateTxRootMatchesTxs(Block block, out Hash256 txRoot)
    {
        return ValidateTxRootMatchesTxs(block.Header, block.Body, out txRoot);
    }
    public static bool ValidateTxRootMatchesTxs(BlockHeader header, BlockBody body, out Hash256 txRoot)
    {
        txRoot = TxTrie.CalculateRoot(body.Transactions);
        return txRoot == header.TxRoot;
    }

    public static bool ValidateUnclesHashMatches(Block block, out Hash256 unclesHash)
    {
        return ValidateUnclesHashMatches(block.Header, block.Body, out unclesHash);
    }

    public static bool ValidateUnclesHashMatches(BlockHeader header, BlockBody body, out Hash256 unclesHash)
    {
        unclesHash = UnclesHash.Calculate(body.Uncles);

        return header.UnclesHash == unclesHash;
    }

    public static bool ValidateWithdrawalsHashMatches(Block block, out Hash256? withdrawalsRoot)
    {
        return ValidateWithdrawalsHashMatches(block.Header, block.Body, out withdrawalsRoot);
    }

    public static bool ValidateWithdrawalsHashMatches(BlockHeader header, BlockBody body, out Hash256? withdrawalsRoot)
    {
        withdrawalsRoot = null;
        if (body.Withdrawals is null)
            return header.WithdrawalsRoot is null;

        withdrawalsRoot = new WithdrawalTrie(body.Withdrawals).RootHash;

        return header.WithdrawalsRoot == withdrawalsRoot;
    }

    private static string Invalid(Block block) =>
        $"Invalid block {block.ToString(Block.Format.FullHashAndNumber)}:";
}
