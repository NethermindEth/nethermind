// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.Validators;

public interface IBlockValidator : IHeaderValidator
{
    bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error);
    bool ValidateSuggestedBlock(Block block, [NotNullWhen(false)] out string? error, bool validateHashes = true);
    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error);

    /// <summary>
    /// Validates the specified block against the underlying <see cref="IWithdrawalValidator"/>.
    /// </summary>
    /// <param name="block">The block to validate.</param>
    /// <param name="error">The validation error message if any.</param>
    /// <returns>Whether the block has valid withdrawals.</returns>
    bool ValidateWithdrawals(Block block, out string? error);

    bool ValidateBody(Block block);
}
