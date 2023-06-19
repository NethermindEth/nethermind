// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    /// Suggested block validation runs basic checks that can be executed before going through the expensive EVM processing.
    /// </summary>
    /// <param name="block">A block to validate</param>
    /// <returns>
    /// <c>true</c> if the <paramref name="block"/> is valid; otherwise, <c>false</c>.
    /// </returns>
    public bool ValidateSuggestedBlock(Block block)
    {
        Transaction[] txs = block.Transactions;
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        for (int i = 0; i < txs.Length; i++)
        {
            if (!_txValidator.IsWellFormed(txs[i], spec))
            {
                if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Invalid transaction {txs[i].Hash}");
                return false;
            }
        }

        if (spec.MaximumUncleCount < block.Uncles.Length)
        {
            _logger.Debug($"{Invalid(block)} Uncle count of {block.Uncles.Length} exceeds the max limit of {spec.MaximumUncleCount}");
            return false;
        }

        if (!ValidateUnclesHashMatches(block, out var unclesHash))
        {
            _logger.Debug($"{Invalid(block)} Uncles hash mismatch: expected {block.Header.UnclesHash}, got {unclesHash}");
            return false;
        }

        if (!_unclesValidator.Validate(block.Header, block.Uncles))
        {
            _logger.Debug($"{Invalid(block)} Invalid uncles");
            return false;
        }

        bool blockHeaderValid = _headerValidator.Validate(block.Header);
        if (!blockHeaderValid)
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Invalid header");
            return false;
        }

        if (!ValidateTxRootMatchesTxs(block, out Keccak txRoot))
        {
            if (_logger.IsDebug) _logger.Debug($"{Invalid(block)} Transaction root hash mismatch: expected {block.Header.TxRoot}, got {txRoot}");
            return false;
        }

        if (!ValidateWithdrawals(block, spec, out _))
            return false;

        if (!ValidateTransactionsDataGas(block, spec, out _))
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
        if (!isValid)
        {
            if (_logger.IsError) _logger.Error($"Processed block {processedBlock.ToString(Block.Format.Short)} is invalid:");
            if (_logger.IsError) _logger.Error($"- hash: expected {suggestedBlock.Hash}, got {processedBlock.Hash}");

            if (processedBlock.Header.GasUsed != suggestedBlock.Header.GasUsed)
            {
                if (_logger.IsError) _logger.Error($"- gas used: expected {suggestedBlock.Header.GasUsed}, got {processedBlock.Header.GasUsed} (diff: {processedBlock.Header.GasUsed - suggestedBlock.Header.GasUsed})");
            }

            if (processedBlock.Header.Bloom != suggestedBlock.Header.Bloom)
            {
                if (_logger.IsError) _logger.Error($"- bloom: expected {suggestedBlock.Header.Bloom}, got {processedBlock.Header.Bloom}");
            }

            if (processedBlock.Header.ReceiptsRoot != suggestedBlock.Header.ReceiptsRoot)
            {
                if (_logger.IsError) _logger.Error($"- receipts root: expected {suggestedBlock.Header.ReceiptsRoot}, got {processedBlock.Header.ReceiptsRoot}");
            }

            if (processedBlock.Header.StateRoot != suggestedBlock.Header.StateRoot)
            {
                if (_logger.IsError) _logger.Error($"- state root: expected {suggestedBlock.Header.StateRoot}, got {processedBlock.Header.StateRoot}");
            }

            if (processedBlock.Header.DataGasUsed != suggestedBlock.Header.DataGasUsed)
            {
                if (_logger.IsError) _logger.Error($"- data gas used: expected {suggestedBlock.Header.DataGasUsed}, got {processedBlock.Header.DataGasUsed}");
            }

            if (processedBlock.Header.ExcessDataGas != suggestedBlock.Header.ExcessDataGas)
            {
                if (_logger.IsError) _logger.Error($"- excess data gas: expected {suggestedBlock.Header.ExcessDataGas}, got {processedBlock.Header.ExcessDataGas}");
            }

            for (int i = 0; i < processedBlock.Transactions.Length; i++)
            {
                if (receipts[i].Error is not null && receipts[i].GasUsed == 0 && receipts[i].Error == "invalid")
                {
                    if (_logger.IsError) _logger.Error($"- invalid transaction {i}");
                }
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
            if (!ValidateWithdrawalsHashMatches(block, out Keccak withdrawalsRoot))
            {
                error = $"Withdrawals root hash mismatch in block {block.ToString(Block.Format.FullHashAndNumber)}: expected {block.Header.WithdrawalsRoot}, got {withdrawalsRoot}";
                if (_logger.IsWarn) _logger.Warn($"Withdrawals root hash mismatch in block {block.ToString(Block.Format.FullHashAndNumber)}: expected {block.Header.WithdrawalsRoot}, got {withdrawalsRoot}");

                return false;
            }
        }

        error = null;

        return true;
    }

    private bool ValidateTransactionsDataGas(Block block, IReleaseSpec spec, out string? error)
    {
        if (!spec.IsEip4844Enabled)
        {
            error = null;
            return true;
        }

        int blobsInBlock = 0;
        UInt256 dataGasPrice = UInt256.Zero;
        for (int txIndex = 0; txIndex < block.Transactions.Length; txIndex++)
        {
            Transaction transaction = block.Transactions[txIndex];
            if (!transaction.SupportsBlobs)
            {
                continue;
            }
            if (dataGasPrice == UInt256.Zero)
            {
                if (!DataGasCalculator.TryCalculateDataGasPricePerUnit(block.Header, out dataGasPrice))
                {
                    error = $"{nameof(dataGasPrice)} overflow.";
                    if (_logger.IsWarn) _logger.Warn(error);
                    return false;
                }
            }
            if (transaction.MaxFeePerDataGas < dataGasPrice)
            {
                error = $"A transaction has unsufficient MaxFeePerDataGas {transaction.MaxFeePerDataGas} < {dataGasPrice}.";
                if (_logger.IsWarn) _logger.Warn(error);
                return false;
            }

            blobsInBlock += transaction.BlobVersionedHashes!.Length;
        }

        ulong dataGasUsed = DataGasCalculator.CalculateDataGas(blobsInBlock);
        if (dataGasUsed > Eip4844Constants.MaxDataGasPerBlock)
        {
            error = $"A block cannot have more than {Eip4844Constants.MaxDataGasPerBlock} data gas.";
            if (_logger.IsWarn) _logger.Warn(error);
            return false;
        }

        if (dataGasUsed != block.Header.DataGasUsed)
        {
            error = $"DataGasUsed does not match actual data gas used: {block.Header.DataGasUsed} != {dataGasUsed}.";
            if (_logger.IsWarn) _logger.Warn(error);
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateTxRootMatchesTxs(Block block, out Keccak txRoot)
    {
        txRoot = new TxTrie(block.Transactions).RootHash;
        return txRoot == block.Header.TxRoot;
    }

    public static bool ValidateUnclesHashMatches(Block block, out Keccak unclesHash)
    {
        unclesHash = UnclesHash.Calculate(block);

        return block.Header.UnclesHash == unclesHash;
    }

    public static bool ValidateWithdrawalsHashMatches(Block block, out Keccak? withdrawalsRoot)
    {
        withdrawalsRoot = null;
        if (block.Withdrawals == null)
            return block.Header.WithdrawalsRoot == null;

        withdrawalsRoot = new WithdrawalTrie(block.Withdrawals).RootHash;

        return block.Header.WithdrawalsRoot == withdrawalsRoot;
    }

    private static string Invalid(Block block) =>
        $"Invalid block {block.ToString(Block.Format.FullHashAndNumber)}:";
}
