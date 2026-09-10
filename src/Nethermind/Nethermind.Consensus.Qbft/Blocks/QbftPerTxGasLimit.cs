// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using ValidationResult = Nethermind.Core.ValidationResult;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>Besu's <c>pertxgaslimit</c>: a transaction may not declare more gas than the cap in force; 0 means unlimited.</summary>
public static class QbftPerTxGasLimit
{
    public static ulong? EffectiveCap(QbftConfigSnapshot config) => config.PerTxGasLimit is { } cap && cap != 0 ? cap : null;
}

/// <summary>Applies the per-transaction cap of the fork after the current head to transactions entering the pool.</summary>
public sealed class QbftPerTxGasLimitTxValidator(ITxValidator inner, QbftForksSchedule forksSchedule, IBlockTree blockTree) : ITxValidator
{
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        Exceeds(transaction) ?? inner.IsWellFormed(transaction, releaseSpec);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit) =>
        Exceeds(transaction) ?? inner.IsWellFormed(transaction, releaseSpec, blockGasLimit);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit, TxValidationOptions options) =>
        Exceeds(transaction) ?? inner.IsWellFormed(transaction, releaseSpec, blockGasLimit, options);

    private ValidationResult? Exceeds(Transaction transaction)
    {
        BlockHeader? head = blockTree.Head?.Header;
        if (head is null) return null;
        ulong? cap = QbftPerTxGasLimit.EffectiveCap(forksSchedule.GetFork((long)head.Number + 1, head.Timestamp));
        if (cap is { } limit && transaction.GasLimit > limit)
        {
            return new ValidationResult($"Transaction gas limit {transaction.GasLimit} exceeds the QBFT per-transaction cap {limit}.");
        }

        return null;
    }
}

/// <summary>Rejects blocks containing a transaction above the per-transaction cap of the block's own fork.</summary>
public sealed class QbftPerTxGasLimitBlockValidator(IBlockValidator inner, QbftForksSchedule forksSchedule) : IBlockValidator
{
    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) => inner.Validate(header, parent, isUncle, out error);

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) => inner.ValidateOrphaned(header, out error);

    public bool ValidateWithdrawals(Block block, [NotNullWhen(false)] out string? error) => inner.ValidateWithdrawals(block, out error);

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) =>
        inner.ValidateOrphanedBlock(block, out error) && ValidateTxGasLimits(block, out error);

    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true) =>
        inner.ValidateSuggestedBlock(block, parent, out error, validateHashes) && ValidateTxGasLimits(block, out error);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) =>
        inner.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) =>
        inner.ValidateBodyAgainstHeader(header, toBeValidated, out error);

    private bool ValidateTxGasLimits(Block block, [NotNullWhen(false)] out string? error)
    {
        ulong? cap = QbftPerTxGasLimit.EffectiveCap(forksSchedule.GetFork((long)block.Number, block.Timestamp));
        if (cap is { } limit)
        {
            foreach (Transaction tx in block.Transactions)
            {
                if (tx.GasLimit > limit)
                {
                    error = $"Transaction {tx.Hash} gas limit {tx.GasLimit} exceeds the QBFT per-transaction cap {limit}.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }
}
